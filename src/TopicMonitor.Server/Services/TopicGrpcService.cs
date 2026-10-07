using Grpc.Core;
using TopicMonitor.Contracts;

namespace TopicMonitor.Server.Services;

/// <summary>
/// Implements the v1 gRPC surface directly over <see cref="TopicMonitor.Core.ITopicBus"/>.
///
/// Namespace note: this file deliberately never writes a bare <c>using TopicMonitor.Core;</c> and always
/// qualifies TopicMonitor.Core types (<c>TopicMonitor.Core.TopicValue</c>, <c>TopicMonitor.Core.Validity</c>,
/// <c>TopicMonitor.Core.DeliveryMode</c>, ...) because every one of those names also exists, unqualified, as a
/// generated proto type in <c>TopicMonitor.Contracts</c> (imported unqualified below, since it's this class's
/// actual API surface) - qualifying one side consistently avoids CS0104 ambiguous-reference errors without
/// resorting to a pile of type aliases.
///
/// `is_snapshot` rule (v1 - a judgment call; the plan leaves the exact meaning open, section 6): true only
/// for the very first <see cref="SampleBatch"/> written on a given <c>Subscribe</c> call, and only when
/// that call requested a replay (<c>from_time</c> set). <see cref="TopicMonitor.Core.ITopicBus.Subscribe"/>
/// doesn't expose a live-vs-replay distinction per sample, so this is the simplest defensible proxy for
/// "this first batch may be a historical sample from the replay, not a fresh live one" - useful to a
/// reconnecting client's UI - without requiring the bus itself to track that distinction.
/// </summary>
public sealed class TopicGrpcService : TopicService.TopicServiceBase
{
    private readonly TopicMonitor.Core.ITopicBus _bus;
    private readonly TimeProvider _timeProvider;

    public TopicGrpcService(TopicMonitor.Core.ITopicBus bus, TimeProvider timeProvider)
    {
        _bus = bus;
        _timeProvider = timeProvider;
    }

    public override Task<TopicCatalog> Describe(DescribeRequest request, ServerCallContext context)
    {
        var snapshot = _bus.GetCatalog();
        var catalog = new TopicCatalog { CatalogVersion = snapshot.CatalogVersion };
        foreach (var entry in snapshot.Topics)
            catalog.Topics.Add(ToTopicInfo(entry));
        return Task.FromResult(catalog);
    }

    public override Task<TimeReply> GetTime(TimeRequest request, ServerCallContext context)
    {
        return Task.FromResult(new TimeReply
        {
            ServerMono = _timeProvider.GetTimestamp(),
            ServerUtcUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            MonoFrequency = _timeProvider.TimestampFrequency,
        });
    }

    public override async Task Subscribe(
        SubscribeRequest request,
        IServerStreamWriter<SampleBatch> responseStream,
        ServerCallContext context)
    {
        var patterns = request.Patterns.Count > 0 ? request.Patterns.ToArray() : new[] { "*" };
        var filter = new TopicMonitor.Core.TopicFilter(patterns);
        var mode = request.Mode == DeliveryMode.Latest ? TopicMonitor.Core.DeliveryMode.Latest : TopicMonitor.Core.DeliveryMode.Lossless;
        long? fromTime = request.HasFromTime ? request.FromTime : null;

        var firstBatch = true;
        var samples = _bus.Subscribe(filter, fromTime, mode, context.CancellationToken);

        try
        {
            await foreach (var sample in samples.ConfigureAwait(false))
            {
                var batch = new SampleBatch
                {
                    Seq = sample.Seq,
                    Source = sample.Source.Value,
                    T = sample.T,
                    TPrev = sample.TPrev,
                    TProcessed = sample.TProcessed,
                    TPublish = _timeProvider.GetTimestamp(),
                    TUtcUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                    IsSnapshot = firstBatch && fromTime.HasValue,
                    CatalogVersion = _bus.GetCatalog().CatalogVersion,
                };
                firstBatch = false;

                foreach (var tv in sample.Values)
                    batch.Values.Add(ToProtoTopicValue(tv));

                await responseStream.WriteAsync(batch).ConfigureAwait(false);
            }
        }
        catch (TopicMonitor.Core.SubscriberOverflowException ex)
        {
            //: "a subscriber behind the buffer is disconnected with a resync code." The
            // client must re-sync via Describe() (catalog may have moved on) and a fresh
            // Subscribe(from_time = last received t) rather than treating this as a generic failure.
            throw new RpcException(new Status(
                StatusCode.Aborted,
                $"Subscriber fell behind its lossless queue and was disconnected ({ex.Message}). " +
                "Resync via Describe() then Subscribe(from_time=<last received t>)."));
        }
    }

    private TopicInfo ToTopicInfo(TopicMonitor.Core.TopicCatalogEntry entry)
    {
        var d = entry.Descriptor;
        var info = new TopicInfo
        {
            Id = (uint)entry.Handle.Id,
            Name = d.Name,
            Type = ToValueType(d.Type),
            Unit = d.Unit,
            Kind = d.Kind == TopicMonitor.Core.TopicKind.Raw ? TopicMonitor.Contracts.TopicKind.Raw : TopicMonitor.Contracts.TopicKind.Derived,
            Producer = d.Producer,
        };

        if (d.DerivedFrom is { Count: > 0 } derivedFrom) info.DerivedFrom.AddRange(derivedFrom);
        if (d.EnumValues is { Count: > 0 } enumValues) info.EnumValues.AddRange(enumValues);
        if (d.Components is { Count: > 0 } components) info.Components.AddRange(components);

        return info;
    }

    private static TopicMonitor.Contracts.ValueType ToValueType(TopicMonitor.Core.TopicType type) => type switch
    {
        TopicMonitor.Core.TopicType.Float => TopicMonitor.Contracts.ValueType.Float,
        TopicMonitor.Core.TopicType.Int => TopicMonitor.Contracts.ValueType.Int,
        TopicMonitor.Core.TopicType.Bool => TopicMonitor.Contracts.ValueType.Bool,
        TopicMonitor.Core.TopicType.String => TopicMonitor.Contracts.ValueType.String,
        TopicMonitor.Core.TopicType.Enum => TopicMonitor.Contracts.ValueType.Enum,
        TopicMonitor.Core.TopicType.Vec => TopicMonitor.Contracts.ValueType.Vec,
        _ => TopicMonitor.Contracts.ValueType.Unspecified,
    };

    private TopicMonitor.Contracts.TopicValue ToProtoTopicValue(TopicMonitor.Core.TopicValue tv)
    {
        var proto = new TopicMonitor.Contracts.TopicValue
        {
            Topic = (uint)tv.Topic.Id,
            Validity = tv.Validity == TopicMonitor.Core.Validity.Valid
                ? TopicMonitor.Contracts.Validity.Valid
                : TopicMonitor.Contracts.Validity.Invalid,
        };

        if (tv.Confidence.HasValue) proto.Confidence = tv.Confidence.Value;
        if (tv.EvidenceSince.HasValue) proto.EvidenceSince = tv.EvidenceSince.Value;

        // An Invalid TopicValue carries a meaningless default Value (see TopicMonitor.Core.TopicValue.Invalid),
        // so the oneof `v` is deliberately left unset (None) rather than encoding a bogus 0/false/"".
        if (tv.Validity == TopicMonitor.Core.Validity.Valid)
        {
            switch (tv.Value.Kind)
            {
                case TopicMonitor.Core.ValueKind.Float:
                    proto.D = tv.Value.AsFloat;
                    break;
                case TopicMonitor.Core.ValueKind.Int:
                    proto.I = tv.Value.AsInt;
                    break;
                case TopicMonitor.Core.ValueKind.Bool:
                    proto.B = tv.Value.AsBool;
                    break;
                case TopicMonitor.Core.ValueKind.String:
                    proto.S = tv.Value.AsString;
                    break;
                case TopicMonitor.Core.ValueKind.Enum:
                {
                    var descriptor = _bus.GetDescriptor(tv.Topic);
                    var index = descriptor.EnumValues is { } values ? IndexOf(values, tv.Value.AsEnum) : -1;
                    if (index >= 0) proto.EnumIndex = (uint)index;
                    break;
                }
                case TopicMonitor.Core.ValueKind.Vec:
                {
                    var vec = new Vec();
                    vec.Values.AddRange(tv.Value.AsVec);
                    proto.Vec = vec;
                    break;
                }
            }
        }

        return proto;
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i] == target) return i;
        return -1;
    }
}

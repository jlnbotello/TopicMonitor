using Grpc.Core;
using Panel.Contracts;

namespace Panel.Server.Services;

public class TopicGrpcService : TopicService.TopicServiceBase
{
    public override Task<TopicCatalog> Describe(DescribeRequest request, ServerCallContext context)
    {
        return Task.FromResult(new TopicCatalog { CatalogVersion = 0 });
    }

    public override Task<TimeReply> GetTime(TimeRequest request, ServerCallContext context)
    {
        return Task.FromResult(new TimeReply
        {
            ServerMono = System.Diagnostics.Stopwatch.GetTimestamp(),
            ServerUtcUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            MonoFrequency = System.Diagnostics.Stopwatch.Frequency,
        });
    }

    public override Task Subscribe(SubscribeRequest request, IServerStreamWriter<SampleBatch> responseStream, ServerCallContext context)
    {
        return Task.CompletedTask;
    }
}

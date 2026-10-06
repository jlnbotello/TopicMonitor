using Panel.Core;

namespace Panel.Sources.File;

/// <summary>A parsed, validated scenario with its topics registered (or re-bound) on a bus, ready to replay.</summary>
public sealed record LoadedScenario(
    SourceId SourceId,
    IReadOnlyDictionary<string, TopicHandle> Handles,
    ScenarioExpander Expander,
    ScenarioDocument Document);

/// <summary>
/// Validates a parsed scenario and registers its topics on the bus. Every topic has exactly one producer (plan
/// section 4): if `bus.Register` reports the name is already taken by another producer, that is surfaced as a
/// clear <see cref="ScenarioValidationException"/> rather than a raw <see cref="DuplicateTopicException"/>.
/// Because <see cref="ITopicBus"/> has no unregister, reloading the *same* file source (edited values, same
/// topic list) must not re-register names it already owns: if a handle for the name already exists and its
/// descriptor is compatible with this file's declaration (same producer, type, kind, enum/vec shape), the
/// existing handle is reused instead of calling Register again.
/// </summary>
public static class ScenarioLoader
{
    public static LoadedScenario Load(ScenarioDocument doc, ITopicBus bus, int noiseSeed = 0)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(bus);

        if (doc.Source is null)
            throw new ScenarioValidationException("Scenario has no '@source' directive.");

        var sourceId = new SourceId(doc.Source.Name);
        var handles = new Dictionary<string, TopicHandle>();

        foreach (var topicDirective in doc.Topics)
        {
            var descriptor = ToDescriptor(topicDirective, doc.Source.Name);
            var existingHandle = bus.TryGetHandle(topicDirective.Name);

            if (existingHandle.HasValue)
            {
                var existing = bus.GetDescriptor(existingHandle.Value);
                if (!IsCompatible(existing, descriptor))
                    throw new ScenarioValidationException(
                        $"Line {topicDirective.Line}, column {topicDirective.Column}: topic '{topicDirective.Name}' " +
                        $"is already registered by producer '{existing.Producer}' with an incompatible declaration.");

                handles[topicDirective.Name] = existingHandle.Value;
            }
            else
            {
                try
                {
                    handles[topicDirective.Name] = bus.Register(descriptor);
                }
                catch (DuplicateTopicException ex)
                {
                    throw new ScenarioValidationException(
                        $"Line {topicDirective.Line}, column {topicDirective.Column}: topic '{ex.TopicName}' " +
                        "is already registered by another producer; a topic has exactly one producer.");
                }
            }
        }

        // Constructing the expander performs full literal/type validation; any failure here is a validation
        // error for the whole file (parsed above), not a partial registration left dangling on the bus.
        var expander = new ScenarioExpander(doc, noiseSeed);
        return new LoadedScenario(sourceId, handles, expander, doc);
    }

    private static bool IsCompatible(TopicDescriptor existing, TopicDescriptor incoming) =>
        existing.Producer == incoming.Producer &&
        existing.Type == incoming.Type &&
        existing.Kind == incoming.Kind &&
        SequenceEqualOrNull(existing.EnumValues, incoming.EnumValues) &&
        SequenceEqualOrNull(existing.Components, incoming.Components);

    private static bool SequenceEqualOrNull(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.SequenceEqual(b);
    }

    private static TopicDescriptor ToDescriptor(ScenarioTopicDirective d, string sourceName)
    {
        var type = d.Type switch
        {
            ScenarioTopicType.Float => TopicType.Float,
            ScenarioTopicType.Int => TopicType.Int,
            ScenarioTopicType.Bool => TopicType.Bool,
            ScenarioTopicType.String => TopicType.String,
            ScenarioTopicType.Enum => TopicType.Enum,
            ScenarioTopicType.Vec => TopicType.Vec,
            _ => throw new ArgumentOutOfRangeException(nameof(d)),
        };

        PublishPolicy? policy = d.Policy?.Kind switch
        {
            ScenarioPolicyKind.Every => PublishPolicy.Every,
            ScenarioPolicyKind.Change => PublishPolicy.OnChange,
            ScenarioPolicyKind.Deadband => PublishPolicy.Deadband(d.Policy.DeadbandThreshold),
            null => null,
            _ => null,
        };

        return new TopicDescriptor(
            d.Name,
            type,
            sourceName,
            EnumValues: d.EnumValues,
            Components: d.Components,
            Policy: policy);
    }
}

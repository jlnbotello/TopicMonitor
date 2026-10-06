namespace Panel.Core;

public enum TopicType
{
    Float,
    Int,
    Bool,
    String,
    Enum,
    Vec,
}

public enum TopicKind
{
    Raw,
    Derived,
}

public enum PublishPolicyKind
{
    Every,
    OnChange,
    Deadband,
}

/// <summary>Declared by each producer when it registers a topic (section 5, "Publish policy").</summary>
public sealed record PublishPolicy(PublishPolicyKind Kind, double DeadbandThreshold = 0)
{
    public static readonly PublishPolicy Every = new(PublishPolicyKind.Every);
    public static readonly PublishPolicy OnChange = new(PublishPolicyKind.OnChange);
    public static PublishPolicy Deadband(double threshold) => new(PublishPolicyKind.Deadband, threshold);
}

/// <summary>
/// Describes one topic at registration time. <see cref="EnumValues"/> applies only to <see cref="TopicType.Enum"/>,
/// <see cref="Components"/> only to <see cref="TopicType.Vec"/> (e.g. ["r","g","b"]).
/// </summary>
public sealed record TopicDescriptor(
    string Name,
    TopicType Type,
    string Producer,
    TopicKind Kind = TopicKind.Raw,
    string Unit = "",
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyList<string>? Components = null,
    PublishPolicy? Policy = null,
    IReadOnlyList<string>? DerivedFrom = null)
{
    public PublishPolicy EffectivePolicy => Policy ?? PublishPolicy.Every;
}

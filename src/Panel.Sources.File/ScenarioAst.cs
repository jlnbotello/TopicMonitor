namespace Panel.Sources.File;

/// <summary>Abstract syntax tree for the `.scn` scenario file format (plan section 4).</summary>

public enum ScenarioTimeUnit
{
    Ms,
    S,
}

/// <summary>A sample line's time: either absolute, or relative to the previous sample line's resolved time.</summary>
public sealed record ScenarioTime(bool Relative, long Amount, ScenarioTimeUnit Unit)
{
    public long ToMilliseconds() => Unit == ScenarioTimeUnit.S ? Amount * 1000 : Amount;
}

public enum ScenarioLiteralKind
{
    Number,
    String,
    Bool,
    Ident,
    Vector,
}

/// <summary>A literal value token: exactly one of the fields below is meaningful, selected by <see cref="Kind"/>.</summary>
public sealed record ScenarioLiteral(
    ScenarioLiteralKind Kind,
    double Number = 0,
    string? Text = null,
    bool Bool = false,
    double[]? Vector = null);

public abstract record ScenarioValueNode;

public sealed record ScenarioLiteralNode(ScenarioLiteral Literal) : ScenarioValueNode;

/// <summary>The `!` marker: invalid until the next assignment to the topic.</summary>
public sealed record ScenarioInvalidNode : ScenarioValueNode
{
    public static readonly ScenarioInvalidNode Instance = new();
}

/// <summary>`blink(v1,v2,freqHz[,duty])`: square wave between v1 and v2; duty defaults to 0.5.</summary>
public sealed record ScenarioBlinkNode(ScenarioValueNode V1, ScenarioValueNode V2, double FrequencyHz, double Duty) : ScenarioValueNode;

/// <summary>`ramp(v1,v2,duration)`: linear interpolation from v1 to v2 over duration, then holds at v2.</summary>
public sealed record ScenarioRampNode(ScenarioValueNode V1, ScenarioValueNode V2, double DurationMs) : ScenarioValueNode;

/// <summary>`flicker(v1,v2,frames)`: alternates between v1 and v2 every `frames` source ticks.</summary>
public sealed record ScenarioFlickerNode(ScenarioValueNode V1, ScenarioValueNode V2, int Frames) : ScenarioValueNode;

public sealed record ScenarioAssign(string Topic, ScenarioValueNode Value, double? Confidence, int Line, int Column);

public sealed record ScenarioSampleLine(ScenarioTime Time, IReadOnlyList<ScenarioAssign> Assigns, int Line);

public enum ScenarioPolicyKind
{
    Every,
    Change,
    Deadband,
}

public sealed record ScenarioPolicy(ScenarioPolicyKind Kind, double DeadbandThreshold = 0);

public sealed record ScenarioSourceDirective(string Name, int Rate);

public enum ScenarioTopicType
{
    Float,
    Int,
    Bool,
    String,
    Enum,
    Vec,
}

/// <summary><see cref="SourceName"/> is the name of whichever '@source' directive preceded this '@topic' in the
/// file (a file may declare several '@source' blocks at different rates; each one owns the topics declared
/// after it, until the next '@source'). Null means this topic appeared before any '@source' directive at
/// all -- always a validation error (see <see cref="ScenarioLoader"/>), never a valid, unowned topic.</summary>
public sealed record ScenarioTopicDirective(
    string Name,
    string? SourceName,
    ScenarioTopicType Type,
    IReadOnlyList<string>? EnumValues,
    IReadOnlyList<string>? Components,
    ScenarioPolicy? Policy,
    int Line,
    int Column);

public sealed record ScenarioNoiseDirective(string Glob, double Sigma);

/// <summary>The fully parsed scenario file: directives plus the ordered list of sample lines. A file may
/// declare several '@source' blocks, each at its own rate (e.g. a fast camera source for LEDs alongside a
/// slower one for text a human reads) -- <see cref="Sources"/> is empty only when the file has no
/// '@source' directive at all, which <see cref="ScenarioLoader"/> rejects.</summary>
public sealed record ScenarioDocument(
    IReadOnlyList<ScenarioSourceDirective> Sources,
    IReadOnlyList<ScenarioTopicDirective> Topics,
    IReadOnlyList<ScenarioNoiseDirective> Noises,
    IReadOnlyList<ScenarioSampleLine> Samples,
    bool Loop);

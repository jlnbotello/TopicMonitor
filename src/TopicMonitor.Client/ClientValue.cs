using TopicMonitor.Contracts;

namespace TopicMonitor.Client;

/// <summary>Discriminator for <see cref="ClientValue"/>, mirroring the proto <c>TopicValue</c> oneof.</summary>
public enum ClientValueKind
{
    None = 0,
    Bool,
    Int,
    Float,
    String,
    Enum,
    Vec,
}

/// <summary>
/// A small tagged union holding one sample's payload, translated from the proto <c>TopicValue</c> oneof.
/// <para>
/// <c>TopicMonitor.Client</c> has no project reference to <c>TopicMonitor.Core</c> (dependency
/// table), so this stands in for <c>TopicMonitor.Core.Value</c> here instead of reusing it. Enum values are kept
/// as their wire index (<see cref="AsEnumIndex"/>); resolving an index to its name requires the catalog
/// (<c>TopicInfo.EnumValues</c>), which this type intentionally knows nothing about.
/// </para>
/// </summary>
public readonly struct ClientValue : IEquatable<ClientValue>
{
    public ClientValueKind Kind { get; }

    private readonly double _number;
    private readonly bool _bool;
    private readonly string? _string;
    private readonly double[]? _vec;

    private ClientValue(ClientValueKind kind, double number, bool boolean, string? str, double[]? vec)
    {
        Kind = kind;
        _number = number;
        _bool = boolean;
        _string = str;
        _vec = vec;
    }

    public static readonly ClientValue None = new(ClientValueKind.None, 0, false, null, null);

    public static ClientValue OfBool(bool v) => new(ClientValueKind.Bool, 0, v, null, null);
    public static ClientValue OfInt(long v) => new(ClientValueKind.Int, v, false, null, null);
    public static ClientValue OfFloat(double v) => new(ClientValueKind.Float, v, false, null, null);
    public static ClientValue OfString(string v) => new(ClientValueKind.String, 0, false, v ?? throw new ArgumentNullException(nameof(v)), null);
    public static ClientValue OfEnum(uint index) => new(ClientValueKind.Enum, index, false, null, null);
    public static ClientValue OfVec(double[] components) => new(ClientValueKind.Vec, 0, false, null, components ?? throw new ArgumentNullException(nameof(components)));

    public bool AsBool => Kind == ClientValueKind.Bool ? _bool : throw Mismatch(ClientValueKind.Bool);
    public long AsInt => Kind == ClientValueKind.Int ? (long)_number : throw Mismatch(ClientValueKind.Int);
    public double AsFloat => Kind == ClientValueKind.Float ? _number : throw Mismatch(ClientValueKind.Float);
    public string AsString => Kind == ClientValueKind.String ? _string! : throw Mismatch(ClientValueKind.String);
    public uint AsEnumIndex => Kind == ClientValueKind.Enum ? (uint)_number : throw Mismatch(ClientValueKind.Enum);
    public double[] AsVec => Kind == ClientValueKind.Vec ? _vec! : throw Mismatch(ClientValueKind.Vec);

    private InvalidOperationException Mismatch(ClientValueKind expected) =>
        new($"ClientValue holds {Kind}, not {expected}.");

    /// <summary>Translates a proto <c>TopicValue</c>'s oneof into a <see cref="ClientValue"/>. An unset
    /// oneof (<c>VOneofCase.None</c>, e.g. an empty tick entry) maps to <see cref="None"/>.</summary>
    public static ClientValue FromProto(TopicValue value) => value.VCase switch
    {
        TopicValue.VOneofCase.B => OfBool(value.B),
        TopicValue.VOneofCase.I => OfInt(value.I),
        TopicValue.VOneofCase.D => OfFloat(value.D),
        TopicValue.VOneofCase.S => OfString(value.S),
        TopicValue.VOneofCase.EnumIndex => OfEnum(value.EnumIndex),
        TopicValue.VOneofCase.Vec => OfVec(value.Vec.Values.ToArray()),
        _ => None,
    };

    public bool Equals(ClientValue other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ClientValueKind.Bool => _bool == other._bool,
            ClientValueKind.Int or ClientValueKind.Float or ClientValueKind.Enum => _number.Equals(other._number),
            ClientValueKind.String => _string == other._string,
            ClientValueKind.Vec => _vec is not null && other._vec is not null && _vec.AsSpan().SequenceEqual(other._vec),
            _ => true,
        };
    }

    public override bool Equals(object? obj) => obj is ClientValue v && Equals(v);

    public override int GetHashCode() => Kind switch
    {
        ClientValueKind.Bool => HashCode.Combine(Kind, _bool),
        ClientValueKind.Int or ClientValueKind.Float or ClientValueKind.Enum => HashCode.Combine(Kind, _number),
        ClientValueKind.String => HashCode.Combine(Kind, _string),
        ClientValueKind.Vec => _vec is null ? HashCode.Combine(Kind) : _vec.Aggregate(HashCode.Combine(Kind), HashCode.Combine),
        _ => HashCode.Combine(Kind),
    };

    public override string ToString() => Kind switch
    {
        ClientValueKind.Bool => _bool.ToString(),
        ClientValueKind.Int => ((long)_number).ToString(),
        ClientValueKind.Float => _number.ToString("G"),
        ClientValueKind.String => _string ?? string.Empty,
        ClientValueKind.Enum => $"#{(uint)_number}",
        ClientValueKind.Vec => _vec is null ? "()" : "(" + string.Join(",", _vec) + ")",
        _ => "<none>",
    };
}

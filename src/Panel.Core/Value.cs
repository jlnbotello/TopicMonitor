namespace Panel.Core;

public enum ValueKind
{
    Float,
    Int,
    Bool,
    String,
    Enum,
    Vec,
}

/// <summary>A tagged union holding one sample's payload, per the v1 value types (no color type: raw LEDs are vec[r,g,b]).</summary>
public readonly struct Value : IEquatable<Value>
{
    public ValueKind Kind { get; }

    private readonly double _number;
    private readonly bool _bool;
    private readonly string? _string;
    private readonly double[]? _vec;

    private Value(ValueKind kind, double number, bool boolean, string? str, double[]? vec)
    {
        Kind = kind;
        _number = number;
        _bool = boolean;
        _string = str;
        _vec = vec;
    }

    public static Value OfFloat(double v) => new(ValueKind.Float, v, false, null, null);
    public static Value OfInt(long v) => new(ValueKind.Int, v, false, null, null);
    public static Value OfBool(bool v) => new(ValueKind.Bool, 0, v, null, null);
    public static Value OfString(string v) => new(ValueKind.String, 0, false, v ?? throw new ArgumentNullException(nameof(v)), null);
    public static Value OfEnum(string name) => new(ValueKind.Enum, 0, false, name ?? throw new ArgumentNullException(nameof(name)), null);
    public static Value OfVec(double[] components) => new(ValueKind.Vec, 0, false, null, components ?? throw new ArgumentNullException(nameof(components)));

    public double AsFloat => Kind == ValueKind.Float ? _number : throw Mismatch(ValueKind.Float);
    public long AsInt => Kind == ValueKind.Int ? (long)_number : throw Mismatch(ValueKind.Int);
    public bool AsBool => Kind == ValueKind.Bool ? _bool : throw Mismatch(ValueKind.Bool);
    public string AsString => Kind == ValueKind.String ? _string! : throw Mismatch(ValueKind.String);
    public string AsEnum => Kind == ValueKind.Enum ? _string! : throw Mismatch(ValueKind.Enum);
    public double[] AsVec => Kind == ValueKind.Vec ? _vec! : throw Mismatch(ValueKind.Vec);

    private InvalidOperationException Mismatch(ValueKind expected) =>
        new($"Value holds {Kind}, not {expected}.");

    public bool Equals(Value other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ValueKind.Float => _number.Equals(other._number),
            ValueKind.Int => _number.Equals(other._number),
            ValueKind.Bool => _bool == other._bool,
            ValueKind.String or ValueKind.Enum => _string == other._string,
            ValueKind.Vec => _vec is not null && other._vec is not null && _vec.AsSpan().SequenceEqual(other._vec),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is Value v && Equals(v);

    public override int GetHashCode() => Kind switch
    {
        ValueKind.Float or ValueKind.Int => HashCode.Combine(Kind, _number),
        ValueKind.Bool => HashCode.Combine(Kind, _bool),
        ValueKind.String or ValueKind.Enum => HashCode.Combine(Kind, _string),
        ValueKind.Vec => _vec is null ? HashCode.Combine(Kind) : _vec.Aggregate(HashCode.Combine(Kind), HashCode.Combine),
        _ => 0,
    };

    public override string ToString() => Kind switch
    {
        ValueKind.Float => _number.ToString("G"),
        ValueKind.Int => ((long)_number).ToString(),
        ValueKind.Bool => _bool.ToString(),
        ValueKind.String => _string!,
        ValueKind.Enum => _string!,
        ValueKind.Vec => "(" + string.Join(",", _vec!) + ")",
        _ => "?",
    };
}

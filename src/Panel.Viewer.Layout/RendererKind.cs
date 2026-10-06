namespace Panel.Viewer.Layout;

/// <summary>The lane renderers from plan.md section 8.</summary>
public enum RendererKind
{
    Step,
    Lines,
    Digital,
    Boxes,
    Swatch,
}

public static class RendererKindExtensions
{
    /// <summary>The literal spelling used for the <c>as:</c> property in <c>layout.yaml</c>.</summary>
    public static string ToYamlString(this RendererKind kind) => kind switch
    {
        RendererKind.Step => "step",
        RendererKind.Lines => "lines",
        RendererKind.Digital => "digital",
        RendererKind.Boxes => "boxes",
        RendererKind.Swatch => "swatch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown renderer kind."),
    };

    /// <summary>Parses the <c>as:</c> property value from <c>layout.yaml</c>.</summary>
    public static RendererKind ParseRenderer(string value) => value switch
    {
        "step" => RendererKind.Step,
        "lines" => RendererKind.Lines,
        "digital" => RendererKind.Digital,
        "boxes" => RendererKind.Boxes,
        "swatch" => RendererKind.Swatch,
        _ => throw new FormatException($"Unknown renderer '{value}'."),
    };
}

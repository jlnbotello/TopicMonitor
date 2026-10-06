namespace Panel.Viewer.Layout;

/// <summary>
/// The renderer-applicability table from plan.md section 8:
///
/// | Renderer | Applies to                              | Default for      |
/// |----------|------------------------------------------|-------------------|
/// | step     | float, int                               | float, int        |
/// | lines    | vec (one line per component)             | vec               |
/// | digital  | bool                                      | bool              |
/// | boxes    | enum, string, float, int                 | enum, string      |
/// | swatch   | vec with 3 components + space            | –                 |
/// </summary>
public static class RendererCatalog
{
    private static readonly Dictionary<RendererKind, LaneValueType[]> Applicability = new()
    {
        [RendererKind.Step] = new[] { LaneValueType.Float, LaneValueType.Int },
        [RendererKind.Lines] = new[] { LaneValueType.Vec },
        [RendererKind.Digital] = new[] { LaneValueType.Bool },
        [RendererKind.Boxes] = new[] { LaneValueType.Enum, LaneValueType.String, LaneValueType.Float, LaneValueType.Int },
        [RendererKind.Swatch] = new[] { LaneValueType.Vec },
    };

    private static readonly Dictionary<LaneValueType, RendererKind> Defaults = new()
    {
        [LaneValueType.Float] = RendererKind.Step,
        [LaneValueType.Int] = RendererKind.Step,
        [LaneValueType.Vec] = RendererKind.Lines,
        [LaneValueType.Bool] = RendererKind.Digital,
        [LaneValueType.Enum] = RendererKind.Boxes,
        [LaneValueType.String] = RendererKind.Boxes,
    };

    /// <summary>The default renderer for a type, used as step 1 of style resolution ("type default renderer").</summary>
    public static RendererKind Default(LaneValueType type) =>
        Defaults.TryGetValue(type, out var renderer)
            ? renderer
            : throw new ArgumentOutOfRangeException(nameof(type), type, "No default renderer for this type.");

    /// <summary>Whether <paramref name="renderer"/> may be used for a lane of value type <paramref name="type"/>.</summary>
    public static bool IsApplicable(RendererKind renderer, LaneValueType type) =>
        Applicability.TryGetValue(renderer, out var types) && types.Contains(type);

    /// <summary>
    /// <c>swatch</c> additionally requires a 3-component vec with a declared color space
    /// (see the "vec with 3 components + space" condition in the table above).
    /// </summary>
    public static bool IsSwatchApplicable(LaneValueType type, int componentCount, string? space) =>
        type == LaneValueType.Vec && componentCount == 3 && !string.IsNullOrEmpty(space);

    /// <summary>All renderers applicable to a given value type.</summary>
    public static IReadOnlyList<RendererKind> ApplicableRenderers(LaneValueType type) =>
        Applicability.Where(kv => kv.Value.Contains(type)).Select(kv => kv.Key).ToList();
}

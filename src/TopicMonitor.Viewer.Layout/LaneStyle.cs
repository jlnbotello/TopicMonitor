namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// A named style from the <c>styles:</c> section of <c>layout.yaml</c>, e.g.
/// <c>red: { fill: red }</c> or <c>"@low": { hatch: true }</c>.
/// Every field is optional: an unset field means "do not change this aspect" when the style
/// is overlaid onto another one (see <see cref="OverlayWith"/> and the 3-step resolution order
/// in plan.md section 8).
/// </summary>
public sealed record LaneStyle(string? Fill = null, string? Border = null, bool? Hatch = null)
{
    /// <summary>Step 1 of style resolution: "type default renderer and neutral style".</summary>
    public static readonly LaneStyle Neutral = new(Fill: "#666666", Border: null, Hatch: false);

    /// <summary>
    /// Applies <paramref name="overlay"/> on top of this style: any field the overlay sets
    /// replaces this style's value for that field; fields the overlay leaves unset are kept.
    /// </summary>
    public LaneStyle OverlayWith(LaneStyle overlay) => new(
        overlay.Fill ?? Fill,
        overlay.Border ?? Border,
        overlay.Hatch ?? Hatch);
}

namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// Style resolution in the exact order:
///
/// 1. Type default: renderer and neutral style.
/// 2. Value rules from the layout, e.g. `red` -&gt; red fill, `blinking` -&gt; dashed border.
/// 3. Quality overlay: low confidence -&gt; hatched; invalid -&gt; grey.
///
/// Steps 2 and 3 look up a style by name in the layout's <c>styles:</c> map: step 2 uses the
/// lane's current value/state name (e.g. an enum value like "red" or "blinking"); step 3 always
/// uses the two literal names "@low" and "@invalid", and always applies last, overlaid on top
/// of whatever steps 1/2 produced.
/// </summary>
public static class StyleResolver
{
    public const string LowConfidenceStyleName = "@low";
    public const string InvalidStyleName = "@invalid";

    public static LaneStyle Resolve(
        IReadOnlyDictionary<string, LaneStyle> styles,
        string? valueKey,
        bool isLowConfidence,
        bool isInvalid)
    {
        // Step 1: type default renderer + neutral style.
        var style = LaneStyle.Neutral;

        // Step 2: value rule, e.g. the lane's enum/bool state name.
        if (valueKey is not null && styles.TryGetValue(valueKey, out var valueStyle))
            style = style.OverlayWith(valueStyle);

        // Step 3: quality overlay, always last, always on top of 1/2.
        if (isLowConfidence && styles.TryGetValue(LowConfidenceStyleName, out var lowStyle))
            style = style.OverlayWith(lowStyle);

        if (isInvalid && styles.TryGetValue(InvalidStyleName, out var invalidStyle))
            style = style.OverlayWith(invalidStyle);

        return style;
    }
}

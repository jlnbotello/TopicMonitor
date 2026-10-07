namespace TopicMonitor.Viewer.Layout;

/// <summary>Combines renderer defaulting and the 3-step style resolution for one lane.</summary>
public static class LaneResolver
{
    /// <summary>The effective renderer: the lane's explicit <c>as:</c>, or the type default (step 1).</summary>
    public static RendererKind ResolveRenderer(ExpandedLane lane, LaneValueType type) =>
        !string.IsNullOrEmpty(lane.As) ? RendererKindExtensions.ParseRenderer(lane.As) : RendererCatalog.Default(type);

    /// <summary>The effective style for a lane given its current value/state and quality.</summary>
    public static LaneStyle ResolveStyle(
        LayoutModel model,
        string? valueKey,
        bool isLowConfidence,
        bool isInvalid) =>
        StyleResolver.Resolve(model.Styles, valueKey, isLowConfidence, isInvalid);
}

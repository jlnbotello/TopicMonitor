namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// One entry of the <c>groups:</c> list. Either <see cref="Lanes"/> is set (the group lists its
/// lanes directly), or <see cref="Use"/>/<see cref="P"/> are set (the group instantiates a
/// template, substituting <see cref="P"/> for <c>{p}</c> in the template's lane topics).
/// </summary>
public sealed record LayoutGroup(
    string Name,
    IReadOnlyList<LaneSpec>? Lanes = null,
    string? Use = null,
    string? P = null)
{
    public bool IsTemplateUse => Use is not null;
}

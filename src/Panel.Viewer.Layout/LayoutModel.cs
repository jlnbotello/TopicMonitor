namespace Panel.Viewer.Layout;

/// <summary>The parsed contents of <c>layout.yaml</c> (plan.md section 8).</summary>
public sealed record LayoutModel(
    TimeSpan Window,
    IReadOnlyDictionary<string, LaneStyle> Styles,
    IReadOnlyDictionary<string, IReadOnlyList<LaneSpec>> Templates,
    IReadOnlyList<LayoutGroup> Groups)
{
    public static LayoutModel Empty { get; } = new(
        TimeSpan.FromSeconds(10),
        new Dictionary<string, LaneStyle>(),
        new Dictionary<string, IReadOnlyList<LaneSpec>>(),
        Array.Empty<LayoutGroup>());
}

namespace Panel.Viewer.Layout;

/// <summary>
/// One entry of a <c>TopicCatalogSnapshot</c>-shaped list, as needed by
/// <see cref="LayoutDefaults.FillMissingLanes"/>. Deliberately independent of
/// <c>Panel.Core</c>/<c>Panel.Client</c> types so this project stays dependency-free.
/// </summary>
public readonly record struct CatalogTopic(string Name, LaneValueType Type);

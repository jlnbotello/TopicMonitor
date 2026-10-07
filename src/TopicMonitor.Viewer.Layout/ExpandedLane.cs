namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// A concrete lane after template expansion: <see cref="Topic"/> has any <c>{p}</c> placeholder
/// already substituted.
/// </summary>
public sealed record ExpandedLane(
    string GroupName,
    string Topic,
    string? As,
    string? Unit,
    IReadOnlyList<double>? Range,
    string? Space);

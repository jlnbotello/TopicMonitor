namespace Panel.Viewer.Layout;

/// <summary>
/// One lane entry as written in <c>layout.yaml</c>, inside either a <c>templates:</c> list
/// (where <see cref="Topic"/> may still contain the <c>{p}</c> placeholder) or a group's
/// <c>lanes:</c> list (where it is already concrete).
/// </summary>
/// <param name="Topic">Topic name, possibly containing <c>{p}</c> (e.g. <c>"{p}.raw"</c>).</param>
/// <param name="As">Explicit renderer override (<c>as:</c>), or null to use the type default.</param>
/// <param name="Unit">Optional display unit (e.g. <c>Hz</c>).</param>
/// <param name="Range">Optional display range, e.g. <c>[0, 10]</c>.</param>
/// <param name="Space">Color space for the <c>swatch</c> renderer (e.g. <c>rgb</c>).</param>
public sealed record LaneSpec(
    string Topic,
    string? As = null,
    string? Unit = null,
    IReadOnlyList<double>? Range = null,
    string? Space = null);

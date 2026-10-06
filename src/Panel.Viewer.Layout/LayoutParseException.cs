namespace Panel.Viewer.Layout;

/// <summary>
/// Reported for a malformed <c>layout.yaml</c> (syntax or schema error), with a 1-based
/// line/column so the caller can point at the offending text — mirrors the scenario-file
/// semantics in plan.md section 4 ("Parse errors report line and column; the server keeps the
/// last valid scenario running"), applied here to layout loading (see <see cref="LayoutLoader"/>).
/// </summary>
public sealed class LayoutParseException(int line, int column, string message)
    : Exception($"{message} (line {line}, column {column})")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string RawMessage { get; } = message;
}

namespace TopicMonitor.Viewer.Layout;

/// <summary>The outcome of one <see cref="LayoutLoader"/> load attempt.</summary>
public sealed record LayoutLoadResult(LayoutDocument? Document, int? ErrorLine, int? ErrorColumn, string? ErrorMessage)
{
    public bool Success => Document is not null;

    public static LayoutLoadResult Ok(LayoutDocument document) => new(document, null, null, null);

    public static LayoutLoadResult Failed(int line, int column, string message) => new(null, line, column, message);
}

/// <summary>
/// Loads <c>layout.yaml</c> and keeps the last valid <see cref="LayoutDocument"/> across a
/// failed reload — mirrors the scenario-file semantics in plan.md section 4 ("Parse errors
/// report line and column; the server keeps the last valid scenario running"), applied here to
/// layout loading (section 8: "load errors report line and column and keep the last valid
/// layout").
/// </summary>
public sealed class LayoutLoader
{
    /// <summary>The most recently successfully loaded document, or null if nothing has loaded yet.</summary>
    public LayoutDocument? Current { get; private set; }

    public LayoutLoadResult LoadFromFile(string path) => LoadFromText(File.ReadAllText(path));

    public LayoutLoadResult LoadFromText(string yamlText)
    {
        try
        {
            var document = LayoutDocument.Parse(yamlText);
            Current = document;
            return LayoutLoadResult.Ok(document);
        }
        catch (LayoutParseException ex)
        {
            // Current is deliberately left untouched: the last valid layout keeps being used.
            return LayoutLoadResult.Failed(ex.Line, ex.Column, ex.RawMessage);
        }
    }
}

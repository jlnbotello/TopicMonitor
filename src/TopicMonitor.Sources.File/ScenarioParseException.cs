namespace TopicMonitor.Sources.File;

/// <summary>A `.scn` syntax error. Reports a 1-based line and column,: "Parse errors report line and column".</summary>
public sealed class ScenarioParseException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public ScenarioParseException(string message, int line, int column)
        : base($"Line {line}, column {column}: {message}")
    {
        Line = line;
        Column = column;
    }
}

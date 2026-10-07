namespace TopicMonitor.Processors;

/// <summary>
/// Configuration for one text stabilizer instance.
/// </summary>
/// <param name="Input">Glob matched against raw string topic names, e.g. "display.line*.raw".</param>
/// <param name="K">Number of consecutive equal samples required before a new text is accepted
/// (default k = 2, same debounce pattern as the color classifier).</param>
public sealed record TextStabilizerConfig(string Input, int K = 2);

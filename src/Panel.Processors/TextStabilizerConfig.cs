namespace Panel.Processors;

/// <summary>
/// Configuration for one text stabilizer instance (plan section 5, "Text stabilizer" row).
/// </summary>
/// <param name="Input">Glob matched against raw string topic names, e.g. "display.line*.raw".</param>
/// <param name="K">Number of consecutive equal samples required before a new text is accepted
/// (plan section 11, resolved: default k = 2, same debounce pattern as the color classifier).</param>
public sealed record TextStabilizerConfig(string Input, int K = 2);

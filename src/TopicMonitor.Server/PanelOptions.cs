namespace TopicMonitor.Server;

/// <summary>
/// Server composition-root configuration (plan sections 2 "Decisions" and 5 "Server internals" /
/// "Processors v1": scenario file path, history retention, color classifier references). Bound from the
/// "Panel" configuration section; every default below matches the plan's own worked example so
/// `dotnet run` works with zero extra setup from a fresh clone.
/// </summary>
public sealed class PanelOptions
{
    /// <summary>
    /// Path to the `.scn` scenario file. Resolved relative to the host's content root (see
    /// <see cref="Hosting.ScenarioPathResolver"/>) when not rooted, which also makes `dotnet run` work the
    /// same whether invoked from the repo root or from the project directory.
    /// </summary>
    public string ScenarioPath { get; set; } = "examples/demo.scn";

    /// <summary>History buffer retention in seconds; null keeps <see cref="TopicMonitor.Core.TopicBus"/>'s own
    /// default (5 minutes, plan section 5).</summary>
    public double? HistoryRetentionSeconds { get; set; }

    /// <summary>
    /// Named RGB reference colors for the color classifier, e.g. "red" -&gt; [240, 20, 20] (plan section 5's
    /// worked example). Defaults to that same worked example so the shipped `examples/demo.scn` classifies
    /// correctly with zero configuration.
    /// </summary>
    public Dictionary<string, double[]> ColorReferences { get; set; } = new()
    {
        ["off"] = new double[] { 0, 0, 0 },
        ["red"] = new double[] { 240, 20, 20 },
        ["green"] = new double[] { 20, 230, 30 },
        ["yellow"] = new double[] { 230, 200, 20 },
    };
}

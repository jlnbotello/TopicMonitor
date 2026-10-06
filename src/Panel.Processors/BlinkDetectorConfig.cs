namespace Panel.Processors;

/// <summary>
/// Configuration for one blink detector instance (plan section 5, "Blink detector" row).
/// </summary>
/// <param name="Input">Glob matched against derived enum color topic names, e.g. "led.*.color".</param>
/// <param name="OffValue">The reference color name that means "off" (matches a key of the color
/// classifier's `references` map, e.g. "off"). Used to tell the Off state apart from Steady.</param>
/// <param name="PeriodTolerance">Relative tolerance between two consecutive half-period durations for the
/// signal to be considered regular/periodic (e.g. 0.25 = 25%). Chosen generously for v1 to tolerate the
/// tick quantization of the scenario source; not implied by the plan, documented here.</param>
/// <param name="FrequencyDeadbandHz">Deadband threshold (Hz) applied to the `*.freq` topic's publish
/// policy (plan: "frequency on change with deadband"). Default 0.5 Hz is a judgment call for v1 (not
/// specified by the plan) — small enough to track real blink-rate changes in the demo scenarios (down to
/// ~1 Hz), large enough to absorb tick-quantization jitter in the measured period.</param>
public sealed record BlinkDetectorConfig(
    string Input,
    string OffValue = "off",
    double PeriodTolerance = 0.25,
    double FrequencyDeadbandHz = 0.5);

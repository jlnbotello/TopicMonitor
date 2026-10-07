namespace TopicMonitor.Tests.Unit.Layout;

/// <summary>Shared sample layout text used across the layout-model tests (mirrors plan.md section 8's example).</summary>
internal static class LayoutFixtures
{
    /// <summary>
    /// The exact example from plan.md section 8, with a few hand-written comments added so that
    /// save tests can assert comments survive a targeted edit.
    /// </summary>
    public const string SampleWithComments = """
        # layout.yaml - demo panel
        window: 10s

        styles:
          red:      { fill: red }      # alarm color
          green:    { fill: green }
          yellow:   { fill: yellow }
          off:      { fill: "#333" }
          blinking: { border: dashed } # flashing state
          "@low":     { hatch: true }
          "@invalid": { fill: "#888" }

        templates:
          led:
            - { topic: "{p}.raw",   as: lines }
            - { topic: "{p}.raw",   as: swatch, space: rgb }
            - { topic: "{p}.color" }
            - { topic: "{p}.freq",  unit: Hz, range: [0, 10] }
            - { topic: "{p}.state" }

        groups:
          - name: Display
            lanes:
              # operator-facing text lines
              - { topic: display.line1.text }
              - { topic: display.line2.text }
          - { name: PWR, use: led, p: led.1 }
          - { name: RUN, use: led, p: led.2 }  # run indicator
          - { name: ERR, use: led, p: led.3 }
        """;
}

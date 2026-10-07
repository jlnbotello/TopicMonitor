using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

public class StyleResolverTests
{
    private static IReadOnlyDictionary<string, LaneStyle> Styles => LayoutDocument.Parse(LayoutFixtures.SampleWithComments).Model.Styles;

    [Fact]
    public void Step1_type_default_applies_when_no_value_rule_or_quality_overlay_matches()
    {
        // A float lane (e.g. led.1.freq) with no matching style name and good quality: neutral style only.
        var style = StyleResolver.Resolve(Styles, valueKey: null, isLowConfidence: false, isInvalid: false);

        style.ShouldBe(LaneStyle.Neutral);
    }

    [Fact]
    public void Step2_value_rule_overlays_the_neutral_style_for_an_enum_lane()
    {
        // An enum lane (e.g. led.1.color) currently at "red".
        var style = StyleResolver.Resolve(Styles, valueKey: "red", isLowConfidence: false, isInvalid: false);

        style.Fill.ShouldBe("red");
    }

    [Fact]
    public void Step2_value_rule_can_set_border_for_a_different_enum_value()
    {
        // led.1.state == "blinking" sets a dashed border but keeps the neutral fill.
        var style = StyleResolver.Resolve(Styles, valueKey: "blinking", isLowConfidence: false, isInvalid: false);

        style.Border.ShouldBe("dashed");
        style.Fill.ShouldBe(LaneStyle.Neutral.Fill);
    }

    [Fact]
    public void Step3_quality_overlay_applies_after_the_value_rule_for_low_confidence()
    {
        // A vec lane (led.1.raw) can still carry a confidence value even without an enum state.
        var style = StyleResolver.Resolve(Styles, valueKey: null, isLowConfidence: true, isInvalid: false);

        style.Hatch.ShouldBe(true);
    }

    [Fact]
    public void Step3_invalid_overlay_overrides_the_fill_set_by_the_value_rule()
    {
        // An enum lane at "red" that then goes invalid: @invalid's fill wins because it is applied last.
        var style = StyleResolver.Resolve(Styles, valueKey: "red", isLowConfidence: false, isInvalid: true);

        style.Fill.ShouldBe("#888");
    }

    [Fact]
    public void Step3_overlay_order_is_value_rule_then_low_confidence_then_invalid()
    {
        // All three layers stack: value rule sets border, low-confidence sets hatch, invalid overrides fill last.
        var style = StyleResolver.Resolve(Styles, valueKey: "blinking", isLowConfidence: true, isInvalid: true);

        style.Border.ShouldBe("dashed");
        style.Hatch.ShouldBe(true);
        style.Fill.ShouldBe("#888");
    }

    [Fact]
    public void A_bool_lane_with_no_matching_style_name_falls_back_to_neutral()
    {
        // A bool/digital lane whose state (e.g. "true") has no corresponding style entry.
        var style = StyleResolver.Resolve(Styles, valueKey: "true", isLowConfidence: false, isInvalid: false);

        style.ShouldBe(LaneStyle.Neutral);
    }

    [Fact]
    public void A_string_lane_quality_overlay_still_applies_without_a_value_rule()
    {
        // A string lane (e.g. display.line1.text) has no value-named style but can still be invalid.
        var style = StyleResolver.Resolve(Styles, valueKey: null, isLowConfidence: false, isInvalid: true);

        style.Fill.ShouldBe("#888");
    }

    [Theory]
    [InlineData(LaneValueType.Float, RendererKind.Step)]
    [InlineData(LaneValueType.Int, RendererKind.Step)]
    [InlineData(LaneValueType.Vec, RendererKind.Lines)]
    [InlineData(LaneValueType.Bool, RendererKind.Digital)]
    [InlineData(LaneValueType.Enum, RendererKind.Boxes)]
    [InlineData(LaneValueType.String, RendererKind.Boxes)]
    public void ResolveRenderer_uses_type_default_when_lane_has_no_explicit_as(LaneValueType type, RendererKind expected)
    {
        var lane = new ExpandedLane("G", "topic", As: null, Unit: null, Range: null, Space: null);

        LaneResolver.ResolveRenderer(lane, type).ShouldBe(expected);
    }

    [Fact]
    public void ResolveRenderer_prefers_explicit_as_over_type_default()
    {
        var lane = new ExpandedLane("G", "topic", As: "boxes", Unit: null, Range: null, Space: null);

        LaneResolver.ResolveRenderer(lane, LaneValueType.Float).ShouldBe(RendererKind.Boxes);
    }
}

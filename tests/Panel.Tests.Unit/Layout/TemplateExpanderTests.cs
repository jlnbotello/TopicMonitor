using Panel.Viewer.Layout;
using Shouldly;

namespace Panel.Tests.Unit.Layout;

public class TemplateExpanderTests
{
    [Fact]
    public void Expand_resolves_direct_group_lanes_and_template_groups()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var lanes = TemplateExpander.Expand(document.Model);

        // Direct group lanes keep their concrete topic untouched.
        lanes.ShouldContain(l => l.GroupName == "Display" && l.Topic == "display.line1.text");
        lanes.ShouldContain(l => l.GroupName == "Display" && l.Topic == "display.line2.text");

        // Each `use: led` group expands the template, substituting {p}.
        lanes.ShouldContain(l => l.GroupName == "PWR" && l.Topic == "led.1.raw" && l.As == "lines");
        lanes.ShouldContain(l => l.GroupName == "PWR" && l.Topic == "led.1.raw" && l.As == "swatch" && l.Space == "rgb");
        lanes.ShouldContain(l => l.GroupName == "PWR" && l.Topic == "led.1.color");
        lanes.ShouldContain(l => l.GroupName == "PWR" && l.Topic == "led.1.freq" && l.Unit == "Hz");
        lanes.ShouldContain(l => l.GroupName == "PWR" && l.Topic == "led.1.state");

        lanes.ShouldContain(l => l.GroupName == "RUN" && l.Topic == "led.2.state");
        lanes.ShouldContain(l => l.GroupName == "ERR" && l.Topic == "led.3.color");

        // 2 Display lanes + 3 groups x 5 template lanes each.
        lanes.Count.ShouldBe(2 + 3 * 5);
    }

    [Fact]
    public void Expand_substitutes_placeholder_only_in_topic()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);
        var lanes = TemplateExpander.Expand(document.Model);

        var freqLane = lanes.Single(l => l.GroupName == "RUN" && l.Topic == "led.2.freq");
        freqLane.Range.ShouldBe(new[] { 0.0, 10.0 });
    }

    [Fact]
    public void Expand_throws_for_group_using_unknown_template()
    {
        var model = new LayoutModel(
            TimeSpan.FromSeconds(10),
            new Dictionary<string, LaneStyle>(),
            new Dictionary<string, IReadOnlyList<LaneSpec>>(),
            new[] { new LayoutGroup("Bad", Use: "missing-template", P: "x") });

        Should.Throw<LayoutModelException>(() => TemplateExpander.Expand(model));
    }

    [Fact]
    public void Expand_throws_for_group_missing_lanes_and_use()
    {
        var model = new LayoutModel(
            TimeSpan.FromSeconds(10),
            new Dictionary<string, LaneStyle>(),
            new Dictionary<string, IReadOnlyList<LaneSpec>>(),
            new[] { new LayoutGroup("Bad") });

        Should.Throw<LayoutModelException>(() => TemplateExpander.Expand(model));
    }
}

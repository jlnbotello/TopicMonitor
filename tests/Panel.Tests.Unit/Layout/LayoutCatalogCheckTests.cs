using Panel.Viewer.Layout;
using Shouldly;

namespace Panel.Tests.Unit.Layout;

public class LayoutCatalogCheckTests
{
    private static LayoutModel Model => LayoutDocument.Parse(LayoutFixtures.SampleWithComments).Model;

    [Fact]
    public void Check_lists_lane_topics_missing_from_the_catalog_and_catalog_topics_without_a_lane()
    {
        var catalog = new[] { "display.line1.text", "led.1.raw", "led.1.color", "extra.topic" };

        var result = LayoutCatalogCheck.Check(Model, catalog);

        result.MissingInCatalog.ShouldContain("display.line2.text");
        result.MissingInCatalog.ShouldContain("led.2.raw");
        result.MissingInCatalog.ShouldNotContain("led.1.raw");
        result.UnusedInLayout.ShouldBe(new[] { "extra.topic" });
        result.IsClean.ShouldBeFalse();
    }

    [Fact]
    public void FindYamlTopic_returns_the_template_pattern_for_a_template_lane()
    {
        var lane = TemplateExpander.Expand(Model).Single(l => l.GroupName == "RUN" && l.Topic == "led.2.state");

        LayoutCatalogCheck.FindYamlTopic(Model, lane).ShouldBe("{p}.state");
    }

    [Fact]
    public void FindYamlTopic_returns_the_concrete_topic_for_a_direct_lane()
    {
        var lane = TemplateExpander.Expand(Model).Single(l => l.Topic == "display.line1.text");

        LayoutCatalogCheck.FindYamlTopic(Model, lane).ShouldBe("display.line1.text");
    }

    [Fact]
    public void FindYamlTopic_returns_null_for_auto_generated_and_ambiguous_lanes()
    {
        var autoLane = new ExpandedLane(LayoutDefaults.FillGroupName, "new.counter", null, null, null, null);
        var ambiguous = TemplateExpander.Expand(Model).First(l => l.GroupName == "PWR" && l.Topic == "led.1.raw");

        LayoutCatalogCheck.FindYamlTopic(Model, autoLane).ShouldBeNull();
        LayoutCatalogCheck.FindYamlTopic(Model, ambiguous).ShouldBeNull();
    }
}

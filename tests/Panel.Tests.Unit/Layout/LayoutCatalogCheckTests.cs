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
    public void FindLaneIndex_counts_template_entries_first_then_direct_lanes()
    {
        var lanes = TemplateExpander.Expand(Model);

        LayoutCatalogCheck.FindLaneIndex(Model, lanes.Single(l => l.GroupName == "RUN" && l.Topic == "led.2.state")).ShouldBe(4);
        LayoutCatalogCheck.FindLaneIndex(Model, lanes.Single(l => l.Topic == "display.line1.text")).ShouldBe(5);
    }

    [Fact]
    public void FindLaneIndex_tells_apart_lanes_that_share_a_topic()
    {
        var lanes = TemplateExpander.Expand(Model).Where(l => l.GroupName == "PWR" && l.Topic == "led.1.raw").ToList();

        LayoutCatalogCheck.FindLaneIndex(Model, lanes.Single(l => l.As == "lines")).ShouldBe(0);
        LayoutCatalogCheck.FindLaneIndex(Model, lanes.Single(l => l.As == "swatch")).ShouldBe(1);
    }

    [Fact]
    public void FindLaneIndex_returns_null_for_auto_generated_lanes()
    {
        var autoLane = new ExpandedLane(LayoutDefaults.FillGroupName, "new.counter", null, null, null, null);

        LayoutCatalogCheck.FindLaneIndex(Model, autoLane).ShouldBeNull();
    }
}

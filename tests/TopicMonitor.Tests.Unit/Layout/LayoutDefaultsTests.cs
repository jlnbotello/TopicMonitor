using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

public class LayoutDefaultsTests
{
    [Fact]
    public void FillMissingLanes_appends_a_lane_for_every_catalog_topic_without_one()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);
        var catalog = new[]
        {
            new CatalogTopic("display.line1.text", LaneValueType.String), // already has a lane
            new CatalogTopic("led.1.raw", LaneValueType.Vec),              // already has a lane (via template)
            new CatalogTopic("led.9.raw", LaneValueType.Vec),              // missing
            new CatalogTopic("new.counter", LaneValueType.Int),            // missing
        };

        var filled = LayoutDefaults.FillMissingLanes(document.Model, catalog);

        var expandedTopics = TemplateExpander.Expand(filled).Select(l => l.Topic).ToHashSet();
        expandedTopics.ShouldContain("led.9.raw");
        expandedTopics.ShouldContain("new.counter");

        var fillGroup = filled.Groups.Single(g => g.Name == LayoutDefaults.FillGroupName);
        fillGroup.Lanes!.Single(l => l.Topic == "led.9.raw").As.ShouldBe(RendererKind.Lines.ToYamlString());
        fillGroup.Lanes!.Single(l => l.Topic == "new.counter").As.ShouldBe(RendererKind.Step.ToYamlString());
    }

    [Fact]
    public void FillMissingLanes_is_a_no_op_when_nothing_is_missing()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);
        var catalog = new[] { new CatalogTopic("display.line1.text", LaneValueType.String) };

        var filled = LayoutDefaults.FillMissingLanes(document.Model, catalog);

        filled.ShouldBeSameAs(document.Model);
    }

    [Fact]
    public void FillMissingLanes_does_not_touch_disk()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);
        var catalog = new[] { new CatalogTopic("brand.new.topic", LaneValueType.Bool) };

        var filled = LayoutDefaults.FillMissingLanes(document.Model, catalog);

        // Purely in-memory: the original document's raw text is unaffected either way.
        filled.ShouldNotBeSameAs(document.Model);
        document.RawText.ShouldBe(LayoutFixtures.SampleWithComments);
    }

    [Fact]
    public void FillMissingLanes_appends_into_the_existing_fill_group_on_a_second_call()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);
        var firstPass = LayoutDefaults.FillMissingLanes(document.Model, new[] { new CatalogTopic("a.topic", LaneValueType.Bool) });
        var secondPass = LayoutDefaults.FillMissingLanes(firstPass, new[]
        {
            new CatalogTopic("a.topic", LaneValueType.Bool),
            new CatalogTopic("b.topic", LaneValueType.Float),
        });

        var fillGroups = secondPass.Groups.Where(g => g.Name == LayoutDefaults.FillGroupName).ToList();
        fillGroups.Count.ShouldBe(1);
        fillGroups[0].Lanes!.Select(l => l.Topic).ShouldBe(new[] { "a.topic", "b.topic" });
    }
}

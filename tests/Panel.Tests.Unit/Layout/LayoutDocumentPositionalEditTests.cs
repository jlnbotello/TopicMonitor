using Panel.Viewer.Layout;
using Shouldly;

namespace Panel.Tests.Unit.Layout;

public class LayoutDocumentPositionalEditTests
{
    [Fact]
    public void WithLaneRenderer_by_index_edits_only_the_second_of_two_lanes_sharing_a_topic()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithLaneRenderer(1, RendererKind.Lines);

        var led = updated.Model.Templates["led"];
        led[0].As.ShouldBe("lines");
        led[1].As.ShouldBe("lines");
        led[1].Space.ShouldBe("rgb");
        updated.RawText.ShouldContain("# run indicator");
    }

    [Fact]
    public void WithAppendedGroup_adds_a_group_after_the_last_entry_and_keeps_existing_text()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithAppendedGroup("Unassigned", new[]
        {
            new LaneSpec("display.line1.raw", As: "boxes"),
            new LaneSpec("odd topic", As: "step"),
        });

        var group = updated.Model.Groups.Last();
        group.Name.ShouldBe("Unassigned");
        group.Lanes!.Select(l => l.Topic).ShouldBe(new[] { "display.line1.raw", "odd topic" });
        group.Lanes![0].As.ShouldBe("boxes");
        updated.RawText.ShouldStartWith(document.RawText.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void WithAppendedGroup_creates_groups_when_the_file_has_none_and_preserves_crlf()
    {
        var text = "window: 5s\r\n";
        var document = LayoutDocument.Parse(text);

        var updated = document.WithAppendedGroup("Unassigned", new[] { new LaneSpec("a.b", As: "step") });

        updated.Model.Groups.Single().Lanes!.Single().Topic.ShouldBe("a.b");
        updated.RawText.Replace("\r\n", string.Empty).ShouldNotContain("\n");
    }
}

using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

/// <summary>
/// , open point 1: GUI saves must preserve hand-written comments and
/// formatting. These tests assert that a lane/style change patches only the touched scalar and
/// leaves every other line of the file -- including every comment -- byte-identical.
/// </summary>
public class LayoutDocumentSaveTests
{
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void WithLaneRenderer_replaces_only_the_existing_as_value_for_that_lane()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithLaneRenderer("{p}.raw", RendererKind.Step);

        updated.RawText.ShouldContain("{ topic: \"{p}.raw\",   as: step }");
        AssertOnlyOneLineChanged(document.RawText, updated.RawText);
        AssertAllCommentLinesPreserved(document.RawText, updated.RawText);

        // The other "{p}.raw" entry (the swatch one) must be untouched.
        updated.RawText.ShouldContain("{ topic: \"{p}.raw\",   as: swatch, space: rgb }");

        // Re-parsing confirms the model picked up the change.
        var laneAfter = TemplateExpander.Expand(updated.Model).Single(l => l.GroupName == "PWR" && l.As == "step");
        laneAfter.Topic.ShouldBe("led.1.raw");
    }

    [Fact]
    public void WithLaneRenderer_inserts_an_as_key_when_the_lane_has_none()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithLaneRenderer("{p}.color", RendererKind.Boxes);

        updated.RawText.ShouldContain("{ topic: \"{p}.color\", as: boxes }");
        AssertOnlyOneLineChanged(document.RawText, updated.RawText);
        AssertAllCommentLinesPreserved(document.RawText, updated.RawText);

        var laneAfter = TemplateExpander.Expand(updated.Model).Single(l => l.GroupName == "RUN" && l.Topic == "led.2.color");
        laneAfter.As.ShouldBe("boxes");
    }

    [Fact]
    public void WithLaneRenderer_on_a_direct_group_lane_only_touches_that_lane()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithLaneRenderer("display.line2.text", RendererKind.Boxes);

        updated.RawText.ShouldContain("{ topic: display.line2.text, as: boxes }");
        updated.RawText.ShouldContain("{ topic: display.line1.text }"); // sibling lane untouched
        AssertOnlyOneLineChanged(document.RawText, updated.RawText);
        AssertAllCommentLinesPreserved(document.RawText, updated.RawText);
    }

    [Fact]
    public void WithStyleField_changes_only_that_styles_value()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        var updated = document.WithStyleField("red", "fill", "crimson");

        updated.RawText.ShouldContain("red:      { fill: crimson }      # alarm color");
        AssertOnlyOneLineChanged(document.RawText, updated.RawText);
        AssertAllCommentLinesPreserved(document.RawText, updated.RawText);

        updated.Model.Styles["red"].Fill.ShouldBe("crimson");
    }

    [Fact]
    public void WithLaneRenderer_throws_for_an_unknown_topic()
    {
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        Should.Throw<InvalidOperationException>(() => document.WithLaneRenderer("no.such.topic", RendererKind.Step));
    }

    private static List<int> AssertOnlyOneLineChanged(string originalText, string newText)
    {
        var originalLines = SplitLines(originalText);
        var newLines = SplitLines(newText);

        newLines.Length.ShouldBe(originalLines.Length, "the edit must not insert or remove any line");

        var changedLineIndexes = Enumerable.Range(0, originalLines.Length)
            .Where(i => originalLines[i] != newLines[i])
            .ToList();

        changedLineIndexes.Count.ShouldBe(1, "exactly one line should differ");
        return changedLineIndexes;
    }

    private static void AssertAllCommentLinesPreserved(string originalText, string newText)
    {
        var originalLines = SplitLines(originalText);
        var newLines = SplitLines(newText);
        var changedLineIndexes = Enumerable.Range(0, originalLines.Length)
            .Where(i => originalLines[i] != newLines[i])
            .ToHashSet();

        for (var i = 0; i < originalLines.Length; i++)
        {
            if (changedLineIndexes.Contains(i))
                continue;

            if (originalLines[i].Contains('#'))
            {
                // Every comment line other than the single changed line must be completely unaffected.
                newLines[i].ShouldBe(originalLines[i]);
            }
        }
    }
}

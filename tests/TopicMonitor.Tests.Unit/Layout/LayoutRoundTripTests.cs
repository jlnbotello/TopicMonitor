using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

/// <summary>
///: "Layout: YAML load, template expansion, style resolution order, renderer
/// applicability per type, save/load round trip." Exercises the full pipeline -- load, expand,
/// resolve styles for float/vec/enum/bool/string lanes, save, reload -- and checks the reloaded
/// model is equivalent to the one that was saved.
/// </summary>
public class LayoutRoundTripTests
{
    [Fact]
    public void Load_expand_resolve_save_reload_round_trip()
    {
        // 1. Load.
        var document = LayoutDocument.Parse(LayoutFixtures.SampleWithComments);

        // 2. Expand templates + groups into concrete lanes.
        var lanes = TemplateExpander.Expand(document.Model);
        lanes.ShouldNotBeEmpty();

        // 3. Resolve renderer + style for a representative lane of each value type.
        var floatLane = lanes.Single(l => l.Topic == "led.1.freq");           // float
        var vecLane = lanes.Single(l => l.Topic == "led.1.raw" && l.As == "lines"); // vec
        var enumLane = lanes.Single(l => l.Topic == "led.1.color");           // enum
        var boolLane = new ExpandedLane("Synthetic", "door.open", As: null, Unit: null, Range: null, Space: null); // bool
        var stringLane = lanes.Single(l => l.Topic == "display.line1.text");  // string

        LaneResolver.ResolveRenderer(floatLane, LaneValueType.Float).ShouldBe(RendererKind.Step);
        LaneResolver.ResolveRenderer(vecLane, LaneValueType.Vec).ShouldBe(RendererKind.Lines);
        LaneResolver.ResolveRenderer(enumLane, LaneValueType.Enum).ShouldBe(RendererKind.Boxes);
        LaneResolver.ResolveRenderer(boolLane, LaneValueType.Bool).ShouldBe(RendererKind.Digital);
        LaneResolver.ResolveRenderer(stringLane, LaneValueType.String).ShouldBe(RendererKind.Boxes);

        LaneResolver.ResolveStyle(document.Model, valueKey: null, isLowConfidence: false, isInvalid: false)
            .ShouldBe(LaneStyle.Neutral);
        LaneResolver.ResolveStyle(document.Model, valueKey: "red", isLowConfidence: false, isInvalid: false)
            .Fill.ShouldBe("red");
        LaneResolver.ResolveStyle(document.Model, valueKey: "blinking", isLowConfidence: true, isInvalid: false)
            .ShouldBe(new LaneStyle(Fill: LaneStyle.Neutral.Fill, Border: "dashed", Hatch: true));
        LaneResolver.ResolveStyle(document.Model, valueKey: null, isLowConfidence: false, isInvalid: true)
            .Fill.ShouldBe("#888");

        // 4. Save: change one lane's renderer via a targeted patch.
        var updated = document.WithLaneRenderer("display.line1.text", RendererKind.Boxes);
        var savePath = Path.Combine(Path.GetTempPath(), $"layout-roundtrip-{Guid.NewGuid():N}.yaml");
        try
        {
            updated.SaveTo(savePath);

            // 5. Reload from disk.
            var reloaded = LayoutDocument.LoadFile(savePath);

            // 6. Equivalent model: same expanded lanes, same styles, same renderer for the changed lane.
            var reloadedLanes = TemplateExpander.Expand(reloaded.Model);
            var originalLanesAfterEdit = TemplateExpander.Expand(updated.Model);

            // Record equality on ExpandedLane/LaneStyle compares list-valued fields (e.g. Range)
            // by reference, so compare the meaningful scalar fields plus the list contents explicitly.
            reloadedLanes.Count.ShouldBe(originalLanesAfterEdit.Count);
            reloadedLanes.Select(l => (l.GroupName, l.Topic, l.As, l.Unit, l.Space))
                .ShouldBe(originalLanesAfterEdit.Select(l => (l.GroupName, l.Topic, l.As, l.Unit, l.Space)));
            for (var i = 0; i < reloadedLanes.Count; i++)
                (reloadedLanes[i].Range ?? Array.Empty<double>())
                    .ShouldBe(originalLanesAfterEdit[i].Range ?? Array.Empty<double>());

            reloaded.Model.Styles.ShouldBe(updated.Model.Styles);
            reloaded.Model.Window.ShouldBe(updated.Model.Window);

            reloadedLanes.Single(l => l.Topic == "display.line1.text").As.ShouldBe("boxes");
            reloaded.RawText.ShouldBe(updated.RawText);
        }
        finally
        {
            File.Delete(savePath);
        }
    }
}

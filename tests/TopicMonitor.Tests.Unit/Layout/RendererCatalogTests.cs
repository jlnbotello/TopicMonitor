using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

public class RendererCatalogTests
{
    [Theory]
    [InlineData(RendererKind.Step, LaneValueType.Float, true)]
    [InlineData(RendererKind.Step, LaneValueType.Int, true)]
    [InlineData(RendererKind.Step, LaneValueType.Vec, false)]
    [InlineData(RendererKind.Lines, LaneValueType.Vec, true)]
    [InlineData(RendererKind.Lines, LaneValueType.Float, false)]
    [InlineData(RendererKind.Digital, LaneValueType.Bool, true)]
    [InlineData(RendererKind.Digital, LaneValueType.String, false)]
    [InlineData(RendererKind.Boxes, LaneValueType.Enum, true)]
    [InlineData(RendererKind.Boxes, LaneValueType.String, true)]
    [InlineData(RendererKind.Boxes, LaneValueType.Float, true)]
    [InlineData(RendererKind.Boxes, LaneValueType.Int, true)]
    [InlineData(RendererKind.Boxes, LaneValueType.Bool, false)]
    [InlineData(RendererKind.Boxes, LaneValueType.Vec, false)]
    [InlineData(RendererKind.Swatch, LaneValueType.Vec, true)]
    [InlineData(RendererKind.Swatch, LaneValueType.Float, false)]
    public void IsApplicable_matches_the_plan_table(RendererKind renderer, LaneValueType type, bool expected)
    {
        RendererCatalog.IsApplicable(renderer, type).ShouldBe(expected);
    }

    [Theory]
    [InlineData(LaneValueType.Float, RendererKind.Step)]
    [InlineData(LaneValueType.Int, RendererKind.Step)]
    [InlineData(LaneValueType.Vec, RendererKind.Lines)]
    [InlineData(LaneValueType.Bool, RendererKind.Digital)]
    [InlineData(LaneValueType.Enum, RendererKind.Boxes)]
    [InlineData(LaneValueType.String, RendererKind.Boxes)]
    public void Default_matches_the_plan_table(LaneValueType type, RendererKind expected)
    {
        RendererCatalog.Default(type).ShouldBe(expected);
    }

    [Fact]
    public void Swatch_requires_three_components_and_a_declared_space()
    {
        RendererCatalog.IsSwatchApplicable(LaneValueType.Vec, componentCount: 3, space: "rgb").ShouldBeTrue();
        RendererCatalog.IsSwatchApplicable(LaneValueType.Vec, componentCount: 2, space: "rgb").ShouldBeFalse();
        RendererCatalog.IsSwatchApplicable(LaneValueType.Vec, componentCount: 3, space: null).ShouldBeFalse();
        RendererCatalog.IsSwatchApplicable(LaneValueType.Float, componentCount: 3, space: "rgb").ShouldBeFalse();
    }

    [Fact]
    public void ApplicableRenderers_for_enum_includes_only_boxes()
    {
        RendererCatalog.ApplicableRenderers(LaneValueType.Enum).ShouldBe(new[] { RendererKind.Boxes });
    }

    [Fact]
    public void RendererKind_round_trips_through_yaml_string()
    {
        foreach (var kind in Enum.GetValues<RendererKind>())
        {
            RendererKindExtensions.ParseRenderer(kind.ToYamlString()).ShouldBe(kind);
        }
    }
}

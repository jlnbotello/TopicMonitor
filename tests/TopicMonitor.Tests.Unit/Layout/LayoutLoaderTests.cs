using TopicMonitor.Viewer.Layout;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Layout;

/// <summary>
/// Mirrors plan.md section 4's scenario-file semantics ("Parse errors report line and column;
/// the server keeps the last valid scenario running"), applied to layout loading (section 8).
/// </summary>
public class LayoutLoaderTests
{
    [Fact]
    public void LoadFromText_returns_the_parsed_document_on_success()
    {
        var loader = new LayoutLoader();

        var result = loader.LoadFromText(LayoutFixtures.SampleWithComments);

        result.Success.ShouldBeTrue();
        result.Document.ShouldNotBeNull();
        loader.Current.ShouldBeSameAs(result.Document);
    }

    [Fact]
    public void LoadFromText_reports_line_and_column_on_a_syntax_error()
    {
        var loader = new LayoutLoader();
        const string broken = "window: 10s\nstyles:\n  red: { fill: red\n"; // unterminated flow mapping

        var result = loader.LoadFromText(broken);

        result.Success.ShouldBeFalse();
        result.ErrorLine.ShouldNotBeNull();
        result.ErrorColumn.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void LoadFromText_reports_line_and_column_on_a_schema_error()
    {
        var loader = new LayoutLoader();
        const string missingTopic = """
            window: 10s
            groups:
              - name: Bad
                lanes:
                  - { as: step }
            """;

        var result = loader.LoadFromText(missingTopic);

        result.Success.ShouldBeFalse();
        result.ErrorLine.ShouldBe(5);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("topic");
    }

    [Fact]
    public void A_failed_reload_keeps_the_last_valid_document()
    {
        var loader = new LayoutLoader();
        loader.LoadFromText(LayoutFixtures.SampleWithComments).Success.ShouldBeTrue();
        var lastGood = loader.Current;

        var result = loader.LoadFromText("not: [valid, yaml");

        result.Success.ShouldBeFalse();
        loader.Current.ShouldBeSameAs(lastGood);
    }
}

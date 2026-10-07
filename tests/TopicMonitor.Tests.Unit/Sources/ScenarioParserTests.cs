using TopicMonitor.Sources.File;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Sources;

public class ScenarioParserTests
{
    [Fact]
    public void Parses_source_topic_noise_and_loop_directives()
    {
        var doc = ScenarioParser.Parse("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @topic display.line1.raw string
            @noise led.*.raw sigma=6
            @loop
            """);

        doc.Sources.ShouldBe(new[] { new ScenarioSourceDirective("cam", 20) });
        doc.Topics.Count.ShouldBe(2);
        doc.Topics[0].Name.ShouldBe("led.1.raw");
        doc.Topics[0].SourceName.ShouldBe("cam");
        doc.Topics[0].Type.ShouldBe(ScenarioTopicType.Vec);
        doc.Topics[0].Components.ShouldBe(new[] { "r", "g", "b" });
        doc.Topics[1].Name.ShouldBe("display.line1.raw");
        doc.Topics[1].Type.ShouldBe(ScenarioTopicType.String);
        doc.Noises.Single().ShouldBe(new ScenarioNoiseDirective("led.*.raw", 6));
        doc.Loop.ShouldBeTrue();
    }

    [Fact]
    public void Parses_multiple_source_blocks_each_owning_the_topics_declared_after_it()
    {
        var doc = ScenarioParser.Parse("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @source ocr rate=2
            @topic display.line1.raw string
            """);

        doc.Sources.ShouldBe(new[]
        {
            new ScenarioSourceDirective("cam", 20),
            new ScenarioSourceDirective("ocr", 2),
        });
        doc.Topics.Single(t => t.Name == "led.1.raw").SourceName.ShouldBe("cam");
        doc.Topics.Single(t => t.Name == "display.line1.raw").SourceName.ShouldBe("ocr");
    }

    [Fact]
    public void Topic_declared_before_any_source_parses_with_a_null_source_name()
    {
        // The parser stays permissive here (no ordering enforced at parse time) so the existing
        // "no @source at all" validation error, raised later by ScenarioLoader, keeps working unchanged;
        // see ScenarioLoaderTests for the "declared before any @source" validation error this produces
        // when the file *does* have a @source directive, just not before this topic.
        var doc = ScenarioParser.Parse("@topic orphan float\n0 orphan=1\n");
        doc.Topics.Single().SourceName.ShouldBeNull();
    }

    [Fact]
    public void Parses_enum_and_bool_topic_types()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=10
            @topic led.1.color enum[off,red,green]
            @topic sensor.ok bool
            0 led.1.color=red sensor.ok=true
            """);

        doc.Topics[0].Type.ShouldBe(ScenarioTopicType.Enum);
        doc.Topics[0].EnumValues.ShouldBe(new[] { "off", "red", "green" });
        doc.Topics[1].Type.ShouldBe(ScenarioTopicType.Bool);

        var assigns = doc.Samples.Single().Assigns;
        var colorLit = ((ScenarioLiteralNode)assigns[0].Value).Literal;
        colorLit.Kind.ShouldBe(ScenarioLiteralKind.Ident);
        colorLit.Text.ShouldBe("red");

        var okLit = ((ScenarioLiteralNode)assigns[1].Value).Literal;
        okLit.Kind.ShouldBe(ScenarioLiteralKind.Bool);
        okLit.Bool.ShouldBeTrue();
    }

    [Theory]
    [InlineData("every", ScenarioPolicyKind.Every)]
    [InlineData("change", ScenarioPolicyKind.Change)]
    public void Parses_simple_policy_kinds(string keyword, ScenarioPolicyKind expected)
    {
        var doc = ScenarioParser.Parse($"@source s rate=10\n@topic t float policy={keyword}\n");
        doc.Topics.Single().Policy!.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parses_deadband_policy_with_threshold()
    {
        var doc = ScenarioParser.Parse("@source s rate=10\n@topic t float policy=deadband(0.5)\n");
        var policy = doc.Topics.Single().Policy!;
        policy.Kind.ShouldBe(ScenarioPolicyKind.Deadband);
        policy.DeadbandThreshold.ShouldBe(0.5);
    }

    [Fact]
    public void Parses_absolute_and_relative_times()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=20
            @topic t vec[r,g,b]
            500 t=(1,1,1)
            +100 t=(2,2,2)
            """);

        doc.Samples[0].Time.ShouldBe(new ScenarioTime(false, 500, ScenarioTimeUnit.Ms));
        doc.Samples[1].Time.ShouldBe(new ScenarioTime(true, 100, ScenarioTimeUnit.Ms));
    }

    [Fact]
    public void Parses_seconds_time_unit()
    {
        var doc = ScenarioParser.Parse("@source s rate=20\n@topic t float\n2s t=1\n");
        doc.Samples.Single().Time.ShouldBe(new ScenarioTime(false, 2, ScenarioTimeUnit.S));
    }

    [Fact]
    public void Parses_vector_string_and_number_literals_in_one_line()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=20
            @topic v vec[r,g,b]
            @topic s1 string
            @topic f float
            0 v=(1,2,3) s1="BOOT" f=3.5
            """);

        var assigns = doc.Samples.Single().Assigns;
        ((ScenarioLiteralNode)assigns[0].Value).Literal.Vector.ShouldBe(new[] { 1.0, 2.0, 3.0 });
        ((ScenarioLiteralNode)assigns[1].Value).Literal.Text.ShouldBe("BOOT");
        ((ScenarioLiteralNode)assigns[2].Value).Literal.Number.ShouldBe(3.5);
    }

    [Fact]
    public void Parses_invalid_marker()
    {
        var doc = ScenarioParser.Parse("@source s rate=20\n@topic t vec[r,g,b]\n0 t=!\n");
        doc.Samples.Single().Assigns.Single().Value.ShouldBeOfType<ScenarioInvalidNode>();
    }

    [Fact]
    public void Parses_confidence_suffix()
    {
        var doc = ScenarioParser.Parse("@source s rate=20\n@topic t float\n0 t=5~0.8\n");
        doc.Samples.Single().Assigns.Single().Confidence.ShouldBe(0.8);
    }

    [Fact]
    public void Parses_blink_generator()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=20
            @topic t vec[r,g,b]
            0 t=blink((240,20,20),(0,0,0),5Hz)
            """);

        var node = (ScenarioBlinkNode)doc.Samples.Single().Assigns.Single().Value;
        node.FrequencyHz.ShouldBe(5.0);
        node.Duty.ShouldBe(0.5);
        ((ScenarioLiteralNode)node.V1).Literal.Vector.ShouldBe(new[] { 240.0, 20.0, 20.0 });
        ((ScenarioLiteralNode)node.V2).Literal.Vector.ShouldBe(new[] { 0.0, 0.0, 0.0 });
    }

    [Fact]
    public void Parses_blink_generator_with_explicit_duty()
    {
        var doc = ScenarioParser.Parse("@source s rate=20\n@topic t float\n0 t=blink(1,0,2Hz,0.3)\n");
        var node = (ScenarioBlinkNode)doc.Samples.Single().Assigns.Single().Value;
        node.Duty.ShouldBe(0.3);
    }

    [Fact]
    public void Parses_ramp_generator_with_ms_duration()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=20
            @topic t vec[r,g,b]
            0 t=ramp((240,20,20),(20,230,30),100ms)
            """);

        var node = (ScenarioRampNode)doc.Samples.Single().Assigns.Single().Value;
        node.DurationMs.ShouldBe(100.0);
    }

    [Fact]
    public void Parses_ramp_generator_with_seconds_duration()
    {
        var doc = ScenarioParser.Parse("@source s rate=20\n@topic t float\n0 t=ramp(0,1,2s)\n");
        var node = (ScenarioRampNode)doc.Samples.Single().Assigns.Single().Value;
        node.DurationMs.ShouldBe(2000.0);
    }

    [Fact]
    public void Parses_flicker_generator()
    {
        var doc = ScenarioParser.Parse("""
            @source s rate=20
            @topic t string
            0 t=flicker("READY","REA0Y",1)
            """);

        var node = (ScenarioFlickerNode)doc.Samples.Single().Assigns.Single().Value;
        node.Frames.ShouldBe(1);
        ((ScenarioLiteralNode)node.V1).Literal.Text.ShouldBe("READY");
        ((ScenarioLiteralNode)node.V2).Literal.Text.ShouldBe("REA0Y");
    }

    [Fact]
    public void Ignores_comments_and_blank_lines()
    {
        var doc = ScenarioParser.Parse("""
            # a full demo
            @source s rate=20   # inline comment

            @topic t float

            0 t=1   # initial value
            """);

        doc.Samples.Single().Assigns.Single().Topic.ShouldBe("t");
    }

    [Fact]
    public void Parses_the_demo_scn_worked_example_without_error()
    {
        var doc = ScenarioParser.Parse("""
            # demo.scn
            @source cam rate=20
            @topic led.1.raw  vec[r,g,b]
            @topic led.2.raw  vec[r,g,b]
            @topic led.3.raw  vec[r,g,b]
            @topic display.line1.raw string
            @topic display.line2.raw string
            @noise led.*.raw sigma=6

            0        led.1.raw=(0,0,0)  led.2.raw=(0,0,0)  led.3.raw=(0,0,0)  display.line1.raw="BOOT"  display.line2.raw=""
            500      led.1.raw=(240,20,20)
            +100     led.1.raw=(0,0,0)
            1200     led.2.raw=(20,230,30)  display.line1.raw="READY"  display.line2.raw="v1.2"
            1500     led.1.raw=blink((240,20,20),(0,0,0),5Hz)
            2000     led.3.raw=(230,200,20)
            4000     led.1.raw=ramp((240,20,20),(20,230,30),100ms)
            4500     led.2.raw=!
            5000     display.line1.raw=flicker("READY","REA0Y",1)
            @loop
            """);

        doc.Sources.ShouldBe(new[] { new ScenarioSourceDirective("cam", 20) });
        doc.Topics.Count.ShouldBe(5);
        doc.Samples.Count.ShouldBe(9);
        doc.Loop.ShouldBeTrue();
    }

    [Fact]
    public void Reports_line_and_column_for_unknown_directive()
    {
        var ex = Should.Throw<ScenarioParseException>(() => ScenarioParser.Parse("@foo bar\n"));
        ex.Line.ShouldBe(1);
        ex.Column.ShouldBe(2);
    }

    [Fact]
    public void Reports_line_and_column_for_unterminated_string()
    {
        var ex = Should.Throw<ScenarioParseException>(() => ScenarioParser.Parse(
            "@source s rate=20\n@topic a string\n0 a=\"hello\n"));
        ex.Line.ShouldBe(3);
        ex.Column.ShouldBe(5);
    }

    [Fact]
    public void Reports_line_and_column_for_invalid_number()
    {
        var ex = Should.Throw<ScenarioParseException>(() => ScenarioParser.Parse(
            "@source s rate=20\n@topic t float\n0 t=12.34.56\n"));
        ex.Line.ShouldBe(3);
        ex.Column.ShouldBe(5);
    }

    [Fact]
    public void Reports_line_and_column_for_unexpected_token_at_statement_start()
    {
        var ex = Should.Throw<ScenarioParseException>(() => ScenarioParser.Parse(
            "@source s rate=20\n@topic t vec[r,g,b]\n0 t (1,2,3)\n"));
        ex.Line.ShouldBe(3);
        ex.Column.ShouldBe(3);
    }
}

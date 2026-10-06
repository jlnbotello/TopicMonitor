using Panel.Core;
using Panel.Sources.File;
using Shouldly;

namespace Panel.Tests.Unit.Sources;

public class ScenarioExpanderTests
{
    private static ScenarioExpander Expand(string text, int noiseSeed = 0)
    {
        var doc = ScenarioParser.Parse(text);
        return new ScenarioExpander(doc, doc.Sources.Single().Name, noiseSeed);
    }

    [Fact]
    public void Quantizes_exact_tick_aligned_times_to_their_tick_index()
    {
        // rate=20 -> 50ms/tick. 500ms is exactly tick 10, 600ms exactly tick 12.
        var expander = Expand("""
            @source s rate=20
            @topic t vec[r,g,b]
            0 t=(0,0,0)
            500 t=(1,1,1)
            +100 t=(2,2,2)
            """);

        expander.Evaluate(9)["t"].Value.AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        expander.Evaluate(10)["t"].Value.AsVec.ShouldBe(new[] { 1.0, 1.0, 1.0 });
        expander.Evaluate(11)["t"].Value.AsVec.ShouldBe(new[] { 1.0, 1.0, 1.0 });
        expander.Evaluate(12)["t"].Value.AsVec.ShouldBe(new[] { 2.0, 2.0, 2.0 });
    }

    [Fact]
    public void Quantizes_a_time_between_ticks_up_to_the_next_tick()
    {
        // rate=20 -> 50ms/tick. 510ms is not tick-aligned, so it snaps up to tick 11 (550ms), not tick 10.
        var expander = Expand("@source s rate=20\n@topic t float\n0 t=0\n510 t=1\n");

        expander.Evaluate(10)["t"].Value.AsFloat.ShouldBe(0.0);
        expander.Evaluate(11)["t"].Value.AsFloat.ShouldBe(1.0);
    }

    [Fact]
    public void Relative_time_is_relative_to_the_previous_lines_resolved_time()
    {
        var expander = Expand("@source s rate=20\n@topic t float\n500 t=1\n+100 t=2\n");
        // 500 -> tick 10; +100 -> 600ms -> tick 12 (not relative to tick10's quantized time, but to 500ms itself).
        expander.Evaluate(11)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(12)["t"].Value.AsFloat.ShouldBe(2.0);
    }

    [Fact]
    public void Values_hold_until_the_next_assignment()
    {
        var expander = Expand("@source s rate=10\n@topic t float\n0 t=1\n1000 t=2\n");
        expander.Evaluate(0)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(5)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(9)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(10)["t"].Value.AsFloat.ShouldBe(2.0);
        expander.Evaluate(50)["t"].Value.AsFloat.ShouldBe(2.0); // holds forever without @loop
    }

    [Fact]
    public void Topic_with_no_event_yet_is_invalid()
    {
        var expander = Expand("@source s rate=10\n@topic t float\n1000 t=1\n");
        var at0 = expander.Evaluate(0)["t"];
        at0.Validity.ShouldBe(Validity.Invalid);
    }

    [Fact]
    public void Bang_marks_invalid_until_the_next_value()
    {
        var expander = Expand("@source s rate=10\n@topic t float\n0 t=1\n500 t=!\n1000 t=2\n");
        expander.Evaluate(4)["t"].Validity.ShouldBe(Validity.Valid);
        expander.Evaluate(5)["t"].Validity.ShouldBe(Validity.Invalid);
        expander.Evaluate(9)["t"].Validity.ShouldBe(Validity.Invalid);
        expander.Evaluate(10)["t"].Validity.ShouldBe(Validity.Valid);
        expander.Evaluate(10)["t"].Value.AsFloat.ShouldBe(2.0);
    }

    [Fact]
    public void Confidence_is_carried_on_the_resolved_value()
    {
        var expander = Expand("@source s rate=10\n@topic t float\n0 t=5~0.8\n");
        expander.Evaluate(0)["t"].Confidence.ShouldBe(0.8f);
    }

    [Fact]
    public void Blink_generator_toggles_between_its_two_values_at_the_given_frequency()
    {
        // rate=20 -> 50ms/tick. 5Hz -> period 200ms -> 4 ticks per period, 2 on/2 off (default duty 0.5).
        var expander = Expand("""
            @source s rate=20
            @topic t vec[r,g,b]
            0 t=blink((240,20,20),(0,0,0),5Hz)
            """);

        expander.Evaluate(0)["t"].Value.AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });
        expander.Evaluate(1)["t"].Value.AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });
        expander.Evaluate(2)["t"].Value.AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        expander.Evaluate(3)["t"].Value.AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        expander.Evaluate(4)["t"].Value.AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 }); // next period
    }

    [Fact]
    public void Blink_generator_runs_until_the_next_assignment()
    {
        var expander = Expand("""
            @source s rate=20
            @topic t vec[r,g,b]
            0 t=blink((1,1,1),(0,0,0),5Hz)
            200 t=(9,9,9)
            """);

        expander.Evaluate(3)["t"].Value.AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        expander.Evaluate(4)["t"].Value.AsVec.ShouldBe(new[] { 9.0, 9.0, 9.0 });
        expander.Evaluate(10)["t"].Value.AsVec.ShouldBe(new[] { 9.0, 9.0, 9.0 });
    }

    [Fact]
    public void Ramp_generator_interpolates_and_then_holds_at_the_end_value()
    {
        // rate=20 -> 50ms/tick, ramp over 100ms = 2 ticks.
        var expander = Expand("@source s rate=20\n@topic t float\n0 t=ramp(0,100,100ms)\n");

        expander.Evaluate(0)["t"].Value.AsFloat.ShouldBe(0.0);
        expander.Evaluate(1)["t"].Value.AsFloat.ShouldBe(50.0);
        expander.Evaluate(2)["t"].Value.AsFloat.ShouldBe(100.0);
        expander.Evaluate(5)["t"].Value.AsFloat.ShouldBe(100.0); // holds at end
    }

    [Fact]
    public void Ramp_generator_interpolates_vectors_componentwise()
    {
        var expander = Expand("@source s rate=20\n@topic t vec[r,g,b]\n0 t=ramp((0,0,0),(100,200,20),100ms)\n");
        expander.Evaluate(1)["t"].Value.AsVec.ShouldBe(new[] { 50.0, 100.0, 10.0 });
    }

    [Fact]
    public void Flicker_generator_alternates_every_n_ticks()
    {
        var expander = Expand("""
            @source s rate=20
            @topic t string
            0 t=flicker("A","B",1)
            """);

        expander.Evaluate(0)["t"].Value.AsString.ShouldBe("A");
        expander.Evaluate(1)["t"].Value.AsString.ShouldBe("B");
        expander.Evaluate(2)["t"].Value.AsString.ShouldBe("A");
        expander.Evaluate(3)["t"].Value.AsString.ShouldBe("B");
    }

    [Fact]
    public void Flicker_generator_with_multiple_frames_per_flip()
    {
        var expander = Expand("@source s rate=20\n@topic t string\n0 t=flicker(\"A\",\"B\",2)\n");
        expander.Evaluate(0)["t"].Value.AsString.ShouldBe("A");
        expander.Evaluate(1)["t"].Value.AsString.ShouldBe("A");
        expander.Evaluate(2)["t"].Value.AsString.ShouldBe("B");
        expander.Evaluate(3)["t"].Value.AsString.ShouldBe("B");
        expander.Evaluate(4)["t"].Value.AsString.ShouldBe("A");
    }

    [Fact]
    public void Loop_wraps_the_timeline_back_to_t_zero_after_the_last_explicit_time()
    {
        // rate=20 -> 50ms/tick; last explicit time is 100ms -> tick 2. The last line's state is observable at
        // tick 2 itself, and the timeline wraps back to t=0 strictly after it (tick 3 == tick 0 again), so the
        // loop period is 3 ticks (0, 1, 2).
        var expander = Expand("""
            @source s rate=20
            @topic t float
            0 t=1
            100 t=2
            @loop
            """);

        expander.Evaluate(0)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(1)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(2)["t"].Value.AsFloat.ShouldBe(2.0);
        // past the loop point: wraps back to t=0's state.
        expander.Evaluate(3)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(4)["t"].Value.AsFloat.ShouldBe(1.0);
        expander.Evaluate(5)["t"].Value.AsFloat.ShouldBe(2.0);
        expander.Evaluate(6)["t"].Value.AsFloat.ShouldBe(1.0);
    }

    [Fact]
    public void Without_loop_the_last_state_holds_forever()
    {
        var expander = Expand("@source s rate=20\n@topic t float\n0 t=1\n100 t=2\n");
        expander.Evaluate(1000)["t"].Value.AsFloat.ShouldBe(2.0);
    }

    [Fact]
    public void Noise_is_deterministic_for_a_given_seed()
    {
        var text = "@source s rate=20\n@topic t vec[r,g,b]\n@noise t sigma=6\n0 t=(100,100,100)\n";
        var a = Expand(text, noiseSeed: 42);
        var b = Expand(text, noiseSeed: 42);

        a.Evaluate(7)["t"].Value.AsVec.ShouldBe(b.Evaluate(7)["t"].Value.AsVec);
    }

    [Fact]
    public void Noise_differs_for_different_seeds_and_is_actually_applied()
    {
        var text = "@source s rate=20\n@topic t vec[r,g,b]\n@noise t sigma=6\n0 t=(100,100,100)\n";
        var a = Expand(text, noiseSeed: 1);
        var b = Expand(text, noiseSeed: 2);

        var va = a.Evaluate(7)["t"].Value.AsVec;
        var vb = b.Evaluate(7)["t"].Value.AsVec;
        va.ShouldNotBe(vb);
        va.ShouldNotBe(new[] { 100.0, 100.0, 100.0 }); // noise actually perturbed the raw value
    }

    [Fact]
    public void Noise_only_applies_to_topics_matching_the_glob()
    {
        var expander = Expand("""
            @source s rate=20
            @topic led.1.raw vec[r,g,b]
            @topic other.raw vec[r,g,b]
            @noise led.*.raw sigma=6
            0 led.1.raw=(100,100,100) other.raw=(100,100,100)
            """, noiseSeed: 7);

        expander.Evaluate(3)["other.raw"].Value.AsVec.ShouldBe(new[] { 100.0, 100.0, 100.0 });
    }

    [Fact]
    public void Rejects_a_literal_whose_kind_does_not_match_the_declared_topic_type()
    {
        Should.Throw<ScenarioValidationException>(() =>
            Expand("@source s rate=10\n@topic t float\n0 t=\"not a number\"\n"));
    }

    [Fact]
    public void Rejects_an_enum_value_not_in_the_declared_set()
    {
        Should.Throw<ScenarioValidationException>(() =>
            Expand("@source s rate=10\n@topic t enum[off,on]\n0 t=unknown\n"));
    }

    [Fact]
    public void Rejects_an_undeclared_topic_in_a_sample_line()
    {
        Should.Throw<ScenarioValidationException>(() =>
            Expand("@source s rate=10\n@topic t float\n0 nope=1\n"));
    }

    [Fact]
    public void Flicker_on_a_slow_source_gets_enough_ticks_to_actually_alternate_before_the_loop_wraps()
    {
        // Regression for examples/demo.scn: the shared loop-wrap point is anchored to the file's last
        // explicit event time (maxAbsMs), quantized to *each source's own* tick period. Without a trailing
        // bare time line pushing that point past the flicker's start, "ocr" (500ms/tick) would get exactly
        // one tick at the flicker's start before wrapping back to t=0 -- never enough to alternate, so
        // "REA0Y" would never actually appear despite the generator being declared. The bare "7000" line
        // (no assigns) exists purely to give ocr's slower tick rate room to show it.
        var doc = ScenarioParser.Parse("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @source ocr rate=2
            @topic display.line1.raw string
            0    led.1.raw=(0,0,0) display.line1.raw="BOOT"
            5000 display.line1.raw=flicker("READY","REA0Y",1)
            7000
            @loop
            """);

        var ocr = new ScenarioExpander(doc, "ocr");

        // 5000ms quantizes to ocr's tick 10 (500ms/tick); each subsequent tick should flip the flicker.
        ocr.Evaluate(10)["display.line1.raw"].Value.AsString.ShouldBe("READY");
        ocr.Evaluate(11)["display.line1.raw"].Value.AsString.ShouldBe("REA0Y");
        ocr.Evaluate(12)["display.line1.raw"].Value.AsString.ShouldBe("READY");
        ocr.Evaluate(13)["display.line1.raw"].Value.AsString.ShouldBe("REA0Y");
    }
}

using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Integration;

/// <summary>
/// "Hot reload" bullet, driven by a real <see cref="FileSystemWatcher"/> against a real
/// temp `.scn` file (no FakeTimeProvider needed here - the point under test is the watcher + parse/swap
/// path, not scenario timing, so this uses real <see cref="TimeProvider.System"/> and real-time polling,
/// matching the existing <c>ServerSmokeTests</c> smoke test's style).
/// </summary>
public class HotReloadTests
{
    [Fact]
    public async Task Valid_file_edit_restarts_replay_and_bumps_catalog_version()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=20
            @topic a.raw float
            0    a.raw=1
            500  a.raw=2
            """, useFakeTime: false);

        var client = await h.ConnectAsync("a.raw");

        // Wait for real replay to actually reach the t=500 change before editing the file, so a later
        // revert-to-1-at-t=0 is unambiguous proof of a restart rather than a race with the old scenario's
        // own natural progression.
        await WaitUntilAsync(() => client.GetState("a.raw")?.Value.AsFloat == 2.0, TimeSpan.FromSeconds(5));

        var versionBefore = client.Catalog!.CatalogVersion;

        // Rewrite with a new topic (bumps catalog_version - same-name topics are reused, not
        // re-registered, see ScenarioLoader) and a different t=0 value (proves replay restarted).
        await System.IO.File.WriteAllTextAsync(h.ScenarioPath, """
            @source cam rate=20
            @topic a.raw float
            @topic b.raw float
            0    a.raw=9
            0    b.raw=42
            """);

        await WaitUntilAsync(() => client.Catalog!.CatalogVersion != versionBefore, TimeSpan.FromSeconds(10));

        client.Catalog!.Topics.Select(t => t.Name).ShouldContain("b.raw");

        // Replay restarted: a.raw is back to its new t=0 value, not continuing from wherever the old
        // scenario had left off (2).
        await WaitUntilAsync(() => client.GetState("a.raw")?.Value.AsFloat == 9.0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Invalid_file_edit_leaves_the_running_scenario_and_catalog_version_unchanged()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=20
            @topic a.raw float
            0 a.raw=7
            """, useFakeTime: false);

        var client = await h.ConnectAsync("a.raw");
        await WaitUntilAsync(() => client.GetState("a.raw")?.Value.AsFloat == 7.0, TimeSpan.FromSeconds(5));

        var versionBefore = client.Catalog!.CatalogVersion;
        var catalogChanged = false;
        client.CatalogChanged += (_, _) => catalogChanged = true;

        // Malformed: no '@source' directive at all, which ScenarioParser/Loader reject outright.
        await System.IO.File.WriteAllTextAsync(h.ScenarioPath, """
            this is not a valid scenario file
            """);

        // Give the watcher + TryLoad a real chance to run (and fail) before asserting nothing changed.
        await Task.Delay(1500);

        h.ScenarioSource.LastErrors.ShouldNotBeEmpty();
        client.Catalog!.CatalogVersion.ShouldBe(versionBefore);
        catalogChanged.ShouldBeFalse();
        client.GetState("a.raw")!.Value.AsFloat.ShouldBe(7.0); // old scenario still running, untouched.
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within the timeout.");
            await Task.Delay(100);
        }
    }
}

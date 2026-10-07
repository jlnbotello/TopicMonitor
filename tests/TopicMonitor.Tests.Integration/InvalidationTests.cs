using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Integration;

/// <summary>
/// Plan section 9's "Invalidation" bullet: stopping a source turns its topics <see cref="Validity.Invalid"/>
/// at the client. Drives <see cref="TopicMonitor.Sources.File.ScenarioFileSource.Stop"/> directly (resolved from
/// the real server's DI container via <see cref="TestHost.ScenarioSource"/>) rather than tearing down the
/// whole host, since that's the actual production hook for "a source that stops" (plan section 5) -
/// <c>ScenarioLoaderHostedService.StopAsync</c> calls the very same method on ordinary host shutdown.
/// </summary>
public class InvalidationTests
{
    [Fact]
    public async Task Stopping_the_source_invalidates_its_topics_at_the_client()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=20
            @topic a.raw float
            0 a.raw=7
            """, useFakeTime: false);

        var client = await h.ConnectAsync("a.raw");
        await WaitUntilAsync(() => client.GetState("a.raw")?.Validity == Validity.Valid
            && client.GetState("a.raw")!.Value.AsFloat == 7.0, TimeSpan.FromSeconds(5));

        h.ScenarioSource.Stop();

        await WaitUntilAsync(() => client.GetState("a.raw")?.Validity == Validity.Invalid, TimeSpan.FromSeconds(5));

        client.GetState("a.raw")!.Validity.ShouldBe(Validity.Invalid);
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

using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Integration;

/// <summary>
/// Real-integration smoke test for `TopicMonitor.Server` ("real Kestrel on a random localhost
/// port"): starts the actual composition root (<c>Program.CreateApp</c> - real Kestrel, real
/// <c>TopicBus</c>, real scenario load of `examples/demo.scn`, real processors) on an ephemeral port,
/// calls <c>Describe()</c> over a real gRPC channel, and asserts the catalog contains both raw topics
/// (declared directly by the scenario file) and derived topics (produced by the processors once they've
/// had a moment to classify the first samples).
///
/// This is deliberately a thin smoke test, not the full protocol/hot-reload/invalidation suite from plan
/// section 9 - those need `TopicMonitor.Client` and land together once that project exists.
/// </summary>
public class ServerSmokeTests
{
    [Fact]
    public async Task Describe_lists_raw_and_derived_topics_from_the_demo_scenario()
    {
        var app = Program.CreateApp(new[] { "--urls=http://127.0.0.1:0", "--environment=Development" });
        await app.StartAsync();

        try
        {
            var addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = addressesFeature!.Addresses.First();

            using var channel = GrpcChannel.ForAddress(address);
            var client = new TopicService.TopicServiceClient(channel);

            // Raw topics are registered the instant the scenario loads; derived topics only appear once
            // the processors have classified at least one sample (color classifier's k=2 debounce, see
            // ), which takes a handful of real 50ms source ticks. Both are asynchronous
            // relative to this test, hence the short real-time poll loop rather than a single Describe().
            var expectedRaw = new[] { "led.1.raw", "led.2.raw", "led.3.raw", "display.line1.raw", "display.line2.raw" };
            var expectedDerived = new[] { "led.1.color", "led.2.color", "led.3.color", "display.line1.text" };

            TopicCatalog? catalog = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                catalog = await client.DescribeAsync(new DescribeRequest());
                var names = catalog.Topics.Select(t => t.Name).ToHashSet();
                if (expectedRaw.All(names.Contains) && expectedDerived.All(names.Contains))
                    break;

                await Task.Delay(250);
            }

            catalog.ShouldNotBeNull();
            var gotNames = catalog!.Topics.Select(t => t.Name).ToList();

            foreach (var name in expectedRaw)
                gotNames.ShouldContain(name);
            foreach (var name in expectedDerived)
                gotNames.ShouldContain(name);

            // Spot-check one raw and one derived TopicInfo's shape, matching the scenario's declarations
            // (examples/demo.scn: "@topic led.1.raw vec[r,g,b]") and the color classifier's registration
            // (TopicMonitor.Processors.ColorClassifierProcessor: enum, derived from the matching raw topic).
            var led1Raw = catalog.Topics.Single(t => t.Name == "led.1.raw");
            led1Raw.Type.ShouldBe(TopicMonitor.Contracts.ValueType.Vec);
            led1Raw.Kind.ShouldBe(TopicKind.Raw);
            led1Raw.Components.ShouldBe(new[] { "r", "g", "b" });

            var led1Color = catalog.Topics.Single(t => t.Name == "led.1.color");
            led1Color.Type.ShouldBe(TopicMonitor.Contracts.ValueType.Enum);
            led1Color.Kind.ShouldBe(TopicKind.Derived);
            led1Color.DerivedFrom.ShouldContain("led.1.raw");
            led1Color.EnumValues.ShouldContain("red");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

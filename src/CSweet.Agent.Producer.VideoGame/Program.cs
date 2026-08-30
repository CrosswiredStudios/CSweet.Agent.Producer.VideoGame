using CSweet.Agent.SDK;
using CSweet.Agent.Producer.VideoGame;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var agent = new SpecialistAgent();
    var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
        agent,
        agent.PrimaryCapability,
        new { });
    Console.WriteLine(result.Value);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<SpecialistAgent>();
await builder.Build().RunAsync();

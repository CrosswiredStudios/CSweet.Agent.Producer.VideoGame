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
    // An empty work assignment must be rejected; it is not a successful execution fixture.
    var path = Path.Combine(AppContext.BaseDirectory, "csweet-plugin.json");
    var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
    var errors = CrosswiredStudios.VideoGame.AgentKit.VideoGameSpecialistConformance.ValidateManifest(
        path, agent.AgentId, agent.DeclaredRoleKey, agent.PrimaryCapability);
    var passed = !result.Succeeded && manifest.Id == agent.AgentId && manifest.Version == agent.Version && errors.Count == 0;
    Console.WriteLine(passed ? $"{agent.AgentId} {agent.Version} self-test passed." : "Agent self-test failed.");
    Environment.ExitCode = passed ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<SpecialistAgent>();
await builder.Build().RunAsync();

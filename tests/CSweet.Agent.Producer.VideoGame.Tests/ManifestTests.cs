using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new SpecialistAgent();

        foreach (var name in new[] { "work.item.read", "work.item.comment" })
            Assert.Contains(manifest.Requires, x => x.Name == name && x.Scope == "team");
        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Equal(agent.Version, typeof(SpecialistAgent).Assembly.GetName().Version?.ToString(3));
        var contextWindow = Assert.Single(manifest.Configuration,
            field => field.Key == "maxContextWindowTokens");
        var outputTokens = Assert.Single(manifest.Configuration,
            field => field.Key == "maxOutputTokens");
        Assert.Equal(220_000, contextWindow.DefaultValue!.Value.GetInt32());
        Assert.Equal(32_000, outputTokens.DefaultValue!.Value.GetInt32());
        Assert.Equal("maxContextWindowTokens", outputTokens.LessThanFieldKey);
        Assert.Contains(agent.PrimaryCapability, manifest.Capabilities);
        Assert.Empty(VideoGameSpecialistConformance.ValidateManifest(
            path, agent.AgentId, agent.DeclaredRoleKey, agent.PrimaryCapability));
        Assert.True(VideoGameSpecialistConformance.StateKeysAreIsolated(
            agent.DeclaredRoleKey, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var required = json.RootElement.GetProperty("requires").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("agent.onboarding.complete.v1", required);
        Assert.Contains(AgentLifecycleEvents.Onboarded,
            json.RootElement.GetProperty("events").GetProperty("subscribes").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("work.item.create", required);
        Assert.Contains("communication.coordination.start-board.v1", required);
        Assert.Contains("work.personal-todo.add.v1", required);
        Assert.Contains("work.personal-todo.defer.v1", required);
        Assert.Contains("platform.artifact-package.submit.v1", required);
        Assert.Contains("com.csweet.work.personal-todo.available.v1",
            json.RootElement.GetProperty("events").GetProperty("subscribes").EnumerateArray()
                .Select(x => x.GetString()));
    }

    [Fact]
    public void Output_budget_uses_configuration_and_stays_below_context_window()
    {
        Assert.Equal(32_000, SpecialistAgent.ResolveOutputTokens(new AgentSettings(
            new Dictionary<string, JsonElement>())));
        Assert.Equal(16_000, SpecialistAgent.ResolveOutputTokens(new AgentSettings(
            new Dictionary<string, JsonElement>
            {
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(16_000)
            })));
        Assert.Equal(24_999, SpecialistAgent.ResolveOutputTokens(new AgentSettings(
            new Dictionary<string, JsonElement>
            {
                ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(25_000),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(32_000)
            })));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               (!File.Exists(Path.Combine(directory.FullName, "csweet-plugin.json")) ||
                !Directory.Exists(Path.Combine(directory.FullName, "src"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}

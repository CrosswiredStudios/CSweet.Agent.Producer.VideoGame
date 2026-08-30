using CSweet.Agent.SDK;
using CSweet.VideoGame.AgentKit;

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

        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Contains(agent.PrimaryCapability, manifest.Capabilities);
        Assert.Empty(VideoGameSpecialistConformance.ValidateManifest(
            path, agent.AgentId, agent.DeclaredRoleKey, agent.PrimaryCapability));
        Assert.True(VideoGameSpecialistConformance.StateKeysAreIsolated(
            agent.DeclaredRoleKey, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
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

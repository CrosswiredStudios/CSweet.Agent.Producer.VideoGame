using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningAssetStrategyTests
{
    [Theory]
    [InlineData("current", 1)]
    [InlineData("foreign", 0)]
    [InlineData("superseded", 0)]
    [InlineData("pending", 0)]
    [InlineData("revised-document", 0)]
    [InlineData("changed-hash", 0)]
    public async Task Only_current_project_strategy_bound_to_current_accepted_vision_reaches_planning(string scenario, int expected)
    {
        var project = Guid.NewGuid(); var documentId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var decision = new DecisionRecord(Guid.NewGuid(), project, "video-game.asset-strategy.v1", "Asset strategy",
            "routine-project-production-strategy", [], "procedural", "procedural", "Decided", "Use code-made assets",
            [new("artifact", documentId, revisionId, "exact-hash", "video-game.vision.v1", "Accepted")],
            null, null, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { Mode = "procedural", QualityBar = "Production assets", LicensingConstraints = new[] { "Original or CC0" } }));
        decision = scenario switch
        {
            "foreign" => decision with { WorkstreamId = Guid.NewGuid() },
            "superseded" => decision with { SupersededByDecisionId = Guid.NewGuid() },
            "pending" => decision with { Status = "Pending" },
            _ => decision
        };
        var document = new ArtifactDocument(documentId, "Vision", "video-game.vision.v1", "Accepted", revisionId,
            null, scenario == "revised-document" ? Guid.NewGuid() : revisionId, null, null,
            [new(revisionId, 1, null, "Accepted scope", scenario == "changed-hash" ? "other" : "exact-hash", "Accepted", DateTimeOffset.UtcNow, null, null)])
            { WorkstreamId = project };
        var runtime = new AgentTestRuntime().RegisterCapability<object, ArtifactDocument>(
            PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(document));
        var directions = await SpecialistAgent.ReadAssetPlanningDirectionsAsync([decision], project, runtime.CreateContext(), default);
        Assert.Equal(expected, directions.Count);
        if (expected == 1)
        {
            Assert.Contains("procedural", directions[0]);
            Assert.Contains("Original or CC0", directions[0]);
            Assert.Contains("preserving the accepted quality", directions[0]);
            Assert.Contains(decision.Id.ToString("D"), directions[0]);
        }
    }
}

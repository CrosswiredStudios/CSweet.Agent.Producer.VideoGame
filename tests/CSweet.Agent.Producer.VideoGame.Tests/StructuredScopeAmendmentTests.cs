using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class StructuredScopeAmendmentTests
{
    private static (RoleRepairRequest Request, GameProposedWorkItemV1[] Proposals) Fixture()
    {
        var (request, _) = RoleReplanningTests.Fixture();
        var source = request.OriginalItems[0];
        var extra = source with { Id = Guid.NewGuid(), TypeKey = "work.workflow-ticket.v1",
            Title = "[plan] Foundation — evidence preparation", ProposalProvenance = null };
        request = request with { ScopeDirection = "Chromium is the only required browser.",
            ScopeAuthorizingTurnId = Guid.NewGuid(), OriginalItems = [.. request.OriginalItems, extra],
            ScopeReplacements = [new(source.Id, "acceptanceCriteria", "Working prototype", "Working prototype in Chromium")] };
        GameProposedWorkItemV1[] proposals = [
            new("plan", "task", "Foundation", "Plan and implement the foundation", ["Wave template committed", "Working prototype in Chromium"], "game-technical-director", [], [], [], []),
            new("game", "task", "Game", "Complete game", ["Playable game"], "game-engineer", [], [], [], ["plan"])];
        return (request, proposals);
    }

    [Fact]
    public void GeneratedWorkflowTitlesNeverBecomeDuplicateProposalIdentities()
    {
        var (request, proposals) = Fixture();
        SpecialistAgent.ValidateScopeAmendmentCoverage(request, proposals, ["60 fps"]);
        var cycle = new GameProductionPlanningCycleV1(request.WorkstreamId, request.TeamId, 1, request.BoardId,
            "profile", Guid.NewGuid(), 1, "package", "production", "game", "fingerprint");
        Assert.Empty(SpecialistAgent.RoleRepairCorrectionFindings(request, new(cycle, proposals, [], ["60 fps"], [], "digest")));
        proposals[0] = proposals[0] with { AcceptanceCriteria = ["Wrong"] };
        var findings = SpecialistAgent.RoleRepairCorrectionFindings(request, new(cycle, proposals, [], ["60 fps"], [], "digest"));
        Assert.DoesNotContain(findings, x => x == "Restore existing ticket .");
    }

    [Fact]
    public void LargeExactReplacementsAreCarriedOnceInSourceBoundArtifact()
    {
        var (request, _) = Fixture();
        var text = string.Concat(Enumerable.Repeat("Complete Chromium requirement. ", 1500));
        request = request with { ScopeReplacements = [request.ScopeReplacements[0] with { Replacement = text }] };
        var cycle = new GameProductionPlanningCycleV1(request.WorkstreamId, request.TeamId, 1, request.BoardId,
            "profile", Guid.NewGuid(), 1, "package", "production", "game", "fingerprint");
        var artifact = SpecialistAgent.RoleRepairArtifact(cycle, [], request);
        var amendment = artifact.Payload.GetProperty("scopeAmendment");
        Assert.Equal(request.ScopeAuthorizingTurnId, amendment.GetProperty("authorizingTurnId").GetGuid());
        Assert.Equal(request.OriginalItems.Count, amendment.GetProperty("sourcePlanningDigests").EnumerateObject().Count());
        Assert.Equal(text, amendment.GetProperty("replacements")[0].GetProperty("replacement").GetString());
        var context = SpecialistAgent.RoleRepairContext(request);
        Assert.DoesNotContain(text, context);
        Assert.Contains(request.ScopeDirection!, context);
        var handoff = SpecialistAgent.BoundPlanningHandoff(context + "\n" + new string('c', 18000), artifact);
        Assert.InRange(handoff.Message.Length, 1, 32768);
        Assert.Equal(artifact.Payload.GetRawText(), handoff.Artifact.Payload.GetRawText());
    }

    [Fact]
    public async Task GeneratedAcceptancePlanningReceivesExactAmendmentWithReplayAndStaleGuards()
    {
        var (request, _) = Fixture();
        var workflow = request.OriginalItems[^1] with { Revision = 4, PlanningRevision = 2 };
        request = request with { OriginalItems = [..request.OriginalItems.SkipLast(1), workflow],
            ScopeReplacements = [new(workflow.Id, "acceptanceCriteria", "Working prototype", "Working prototype in Chromium")] };
        var items = request.OriginalItems.ToArray();
        var writes = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(
                JsonSerializer.Deserialize<WorkBoardDetail>("{}")! with { Items = items }))
            .RegisterCapability<ReviseWorkItemPlanningRequest, WorkItem>(WorkItemCapabilities.RevisePlanning, (change, _) =>
            {
                Assert.Equal(workflow.Id, change.ItemId);
                Assert.Equal(4, change.ExpectedRevision);
                Assert.Equal(2, change.ExpectedPlanningRevision);
                writes++;
                items[^1] = items[^1] with { Planning = change.Planning, Revision = 5, PlanningRevision = 3 };
                return Task.FromResult(items[^1]);
            });
        await SpecialistAgent.RefreshAmendedWorkflowPlanningAsync(request, request.BoardId, runtime.CreateContext(), default);
        await SpecialistAgent.RefreshAmendedWorkflowPlanningAsync(request, request.BoardId, runtime.CreateContext(), default);
        Assert.Equal(1, writes);
        Assert.Contains("Working prototype in Chromium", items[^1].Planning!.AcceptanceCriteria);
        items[^1] = workflow with { Planning = workflow.Planning! with { Requirements = ["Changed concurrently"] } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecialistAgent.RefreshAmendedWorkflowPlanningAsync(
            request, request.BoardId, runtime.CreateContext(), default));
        Assert.Equal(1, writes);
    }
}

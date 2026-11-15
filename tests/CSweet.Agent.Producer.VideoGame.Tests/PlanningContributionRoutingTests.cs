using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningContributionRoutingTests
{
    [Theory]
    [InlineData(false, "valid", "Completed")][InlineData(true, "valid", "Completed")]
    [InlineData(true, "wrong-key", "Blocked")][InlineData(true, "wrong-source", "Blocked")]
    [InlineData(true, "wrong-manager", "Blocked")][InlineData(true, "specialist-blocked", "Blocked")]
    public async Task ContributionRepliesAndFinalizationUseTheSharedProtocolInsteadOfPitchRefinement(bool finalization, string scenario, string disposition)
    {
        var project = Guid.NewGuid(); var board = Guid.NewGuid(); var doc = Guid.NewGuid(); var rev = Guid.NewGuid();
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Gabriel", "Producer");
        var specialist = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director");
        var question = new WorkContributionRequest(project, board, "scope", "Own the accepted input obligation", [new(doc, rev, "sha")]);
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 2, 2, "Reconcile planning responsibility", "Own the input", [], self, specialist, finalization, [
            new(Guid.NewGuid(), 0, self.OrganizationUserId, "Continue", "Question", DateTimeOffset.UtcNow,
                new(WorkContributions.RequestType, "1.0", "bound", 1, true, JsonSerializer.SerializeToElement(question), "request")),
            new(Guid.NewGuid(), 1, specialist.OrganizationUserId, scenario == "specialist-blocked" ? "Blocked" : "Completed", "Answer", DateTimeOffset.UtcNow,
                new(WorkContributions.AnswerType, "1.0", scenario == "wrong-key" ? "other" : "bound", 1, true,
                    JsonSerializer.SerializeToElement(new WorkContributionAnswer("Existing owner", "Engineering verifies mouse and keyboard input", "Execute later against the exact build")), "advice")) ]) {
            SourceKind = "Board", BoardSource = new(board), WorkContext = new(Guid.NewGuid(), project, null, board, null, null, null, Guid.NewGuid(), null, null) };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(new WorkBoardDetail(
                new(board, "Game", "", false, false, 1, []) { WorkstreamId = project, ManagerOrganizationUserId = scenario == "wrong-manager" ? Guid.NewGuid() : self.OrganizationUserId }, [], [])))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(new ArtifactDocument(doc, "Brief", "scope", "Accepted", rev, null,
                scenario == "wrong-source" ? Guid.NewGuid() : rev, null, null, [new(rev, 1, null, "Accepted input", "sha", "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = project }));
        // No LLM, pitch, or hiring capabilities are registered: receipt cannot reopen those workflows.
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(request, runtime.CreateContext(), default);
        Assert.Equal(disposition, result.Disposition); Assert.DoesNotContain("Pitch refinement", result.Content);
    }

    [Fact]
    public async Task RetainedProposalFactsFollowAuthoritativeDecisionReplacementsInsteadOfCachedTerminalStatus()
    {
        var project = Guid.NewGuid(); var old = Guid.NewGuid(); var next = Guid.NewGuid();
        var decision = JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Id = old, WorkstreamId = project, Status = "Superseded", SupersededByDecisionId = next };
        var current = decision with { Id = next, Status = "Decided", SupersededByDecisionId = null, Summary = "Current staffing is approved", Rationale = "Draft the approaching scope" };
        var action = new SpecialistAgent.ReviewActionReceipt("escalate", old, "Decided", "old-review") { Rationale = "No engineer exists" };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                new(Guid.NewGuid(), r.StateKey, "review", 1, "Active", new Dictionary<string, string>(), [], "", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new SpecialistAgent.ReviewProposals([action])), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
        var fact = Assert.Single(await SpecialistAgent.RefreshReviewProposalsAsync(project, [decision, current], runtime.CreateContext(), default));
        Assert.Equal(next, fact.Id); Assert.Contains("Draft the approaching scope", fact.Detail); Assert.DoesNotContain("No engineer exists", fact.Detail);
    }
}

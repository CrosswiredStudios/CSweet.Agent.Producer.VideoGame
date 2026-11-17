using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Theory]
    [InlineData("Ready",0)][InlineData("PendingApproval",0)]
    [InlineData("Ready",1)][InlineData("Ready",4)]
    public async Task DraftSelectionUsesCurrentStaffingAndCanProceedBeforeResourceExecutionApproval(string resourceStatus, int changes)
    {
        var f = new Fixture(adaptive: true, independent: true);
        var author = Guid.NewGuid(); var briefId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, briefId, revisionId, "brief", "handoff", Guid.NewGuid(), f.Cycle.ApprovedPackageId, 1, DateTimeOffset.UtcNow);
        var staleRoster = f.Roster;
        var roster = staleRoster with { Revision = 9, LeadEmployeeId = author.ToString(), Members = [
            new("engineer", "Adrian", "Agent", null, null, "Member", "Available") { DeclaredRoleKeys = ["game-engineer"], EffectiveCapabilities = ["work.execution.run"] }], TotalMemberCount = 1 };
        var staffing = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with { Id = Guid.NewGuid(), WorkstreamId = f.Cycle.WorkstreamId,
            TeamId = f.Cycle.TeamId, Status = "Approved", DeliveryStatus = "Fulfilled", Roles = [] };
        var resource = JsonSerializer.Deserialize<ResourceRequestState>("{}")! with { Id = Guid.NewGuid(), WorkstreamId = f.Cycle.WorkstreamId, Status = resourceStatus, Revision = 1 };
        var states = new Dictionary<string, AgentOperatingStateResponse>(); var models = 0;
        var reads = 0;
        f.Runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(roster with { Revision = roster.Revision + Math.Min(reads++,changes) }, f.Cycle.WorkstreamId)))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([staffing])))
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([resource]))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(new ArtifactDocument(briefId, "Brief", "scope", "Accepted", revisionId, null, revisionId, null, null,
                [new(revisionId, 1, null, "This planning document grants no staffing, build or spending permission. Plan a playable prototype.", "brief", "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = f.Cycle.WorkstreamId }))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) => {
                models++; var text = r.GetRawText(); Assert.Contains("Adrian", text); Assert.Contains("Fulfilled", text); Assert.Contains(resourceStatus, text);
                return Task.FromResult<object>(new { text = JsonSerializer.Serialize(new SpecialistAgent.ReviewChoice("Draft the prototype", 0, false, true, "After estimates and resource readiness", "No build has run", [new("proceed", "Draft before execution readiness", null, [0])]) { Disposition = "select" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            });
        var agent = new SpecialistAgent();
        Assert.True((await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))).Succeeded);
        var progress = new ProgressivePlanningProgress(f.Evidence.ScopeKey, f.Evidence.OutlineDigest, f.Evidence.DirectionsDigest, []);
        var choices = SpecialistAgent.SuccessorCandidates(f.Outline.DeliveryItems, new HashSet<string>());
        var board = new WorkBoardDetail(new(f.Cycle.BoardId, "Game", "", false, false, 1, []) { WorkstreamId = f.Cycle.WorkstreamId, TeamId = f.Cycle.TeamId }, [], []);
        async Task<SpecialistAgent.ReviewEffects> Review() => await agent.ReviewAdaptivePlanAsync(f.Cycle, staleRoster, progress, choices, board, [], [], f.Evidence, handoff, f.Runtime.CreateContext(), default);
        var first = await Review();
        if (changes == 4)
        {
            Assert.Equal("wait",first.Disposition); Assert.True(first.KeepDraft); Assert.Null(first.Story);
            Assert.Equal(2,models); Assert.Equal(4,reads); return;
        }
        Assert.Equal(choices[0].ProposalKey, first.Story);
        Assert.Equal(choices[0].ProposalKey, (await Review()).Story); Assert.Equal(changes == 0 ? 1 : 2, models);
        Assert.Equal(resourceStatus, resource.Status); // Draft selection never approved or provisioned anything.
    }
}

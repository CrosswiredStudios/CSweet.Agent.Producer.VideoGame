using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ProjectFoundationTests
{
    [Fact]
    public async Task MissedRevisionEventIsRecoveredFromDurableIndexWithoutDuplicateResubmission()
    {
        var producer = Guid.NewGuid(); var manager = Guid.NewGuid(); var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var models = 0;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new WorkstreamPlanProposalV2Request("Game", "Original outcome", ["Playable"], "Concept", producer, null, [], [],
            null, null, null, null, "Deliver", "original", "game", 1, JsonSerializer.SerializeToElement(new { }),
            new(null, 14, [], ["launch"], ["work-planning"], null), [], []);
        var indexKey = "producer-submitted-project-proposals";
        var cache = new Dictionary<string, AgentOperatingStateResponse> { [indexKey] = new(Guid.NewGuid(), indexKey, "test", 1, "Active",
            new Dictionary<string, string>(), [], "index", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { proposalIds = new[] { oldId } }), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        var submissions = new List<WorkstreamPlanProposalV2Request>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(cache.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(cache[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, JsonElement[]>("platform.project-approval.read.v1", (r, _) => Task.FromResult(
                r.GetProperty("proposalId").GetGuid() == oldId ? new[] { JsonSerializer.SerializeToElement(new { status = "Cancelled",
                    binding = new { payload = plan }, decision = new { decision = "RequestRevision", comment = "Make acceptance measurable." } }, json) } : []))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                models++; Assert.Contains("Make acceptance measurable", r.GetRawText());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(plan with { Outcome = "Measurable delivery outcome" }, json), role = "assistant" })); })
            .RegisterCapability<WorkstreamPlanProposalV2Request, MutationResponse>("platform.workstream.plan.propose.v2", (r, _) => {
                submissions.Add(r); return Task.FromResult(new MutationResponse(false, 0, newId, "Pending")); });
        var context = runtime.CreateContext(identity: new(producer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Director"));
        var agent = new SpecialistAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), context, default);
        Assert.True(configured.Succeeded, configured.Error);
        await agent.RecoverSubmittedProjectsAsync(context, default);
        await agent.RecoverSubmittedProjectsAsync(context, default);
        Assert.Equal(1, models);
        var revised = Assert.Single(submissions);
        Assert.Equal("Measurable delivery outcome", revised.Outcome);
        Assert.Equal(producer, revised.AccountableManagerOrganizationUserId);
        Assert.Contains("launch", revised.AuthorityEnvelope.HumanRequiredActionKeys);
        Assert.Contains(newId.ToString(), cache[indexKey].Payload.GetRawText());
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 3, 3, "Create project", "Review revision", [],
            new(producer, Guid.NewGuid(), "Producer", "Producer"), new(manager, Guid.NewGuid(), "Director", "Director"), false,
            [new(Guid.NewGuid(), 0, manager, "Continue", "Prepare proposal", DateTimeOffset.UtcNow,
                new("video-game.project-foundation.request.v1", "1.0", "vision", 1, true, JsonSerializer.SerializeToElement(plan, json), "digest")),
             new(Guid.NewGuid(), 2, manager, "Continue", "Make acceptance measurable.", DateTimeOffset.UtcNow,
                new("video-game.project-foundation.decision.v1", "1.0", "vision", 1, true,
                    JsonSerializer.SerializeToElement(new { proposalId = oldId, decisionKind = "RequestRevision" }), "decision-digest"))]);
        var coordinated = await agent.HandleCoordinationTurnAsync(request, context, default);
        Assert.Equal("Continue", coordinated.Disposition);
        Assert.Equal(newId, coordinated.Artifact!.Payload.GetProperty("proposalId").GetGuid());
        Assert.Equal(1, models);
        Assert.Equal(2, submissions.Count);
        Assert.Equal(submissions[0].IdempotencyKey, submissions[1].IdempotencyKey);
    }

    [Fact]
    public async Task ProducerOwnsSubmissionAndModelCannotReplaceAuthorityIdentityOrEvidence()
    {
        var producer = Guid.NewGuid(); var manager = Guid.NewGuid(); var proposalId = Guid.NewGuid(); var team = Guid.NewGuid();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var template = new WorkstreamPlanProposalV2Request("Game", "Accepted prototype", ["Playable"], "Concept", producer, team, [], [],
            null, null, null, null, "Deliver", "template", "game", 1, JsonSerializer.SerializeToElement(new { }),
            new(null, 14, [], ["launch"], ["work-planning"], null), [], []);
        var cached = new Dictionary<string, AgentOperatingStateResponse>(); var models = 0; var submitted = new List<WorkstreamPlanProposalV2Request>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(cached.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(cached[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) => {
                models++; return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(template with {
                    AccountableManagerOrganizationUserId = Guid.NewGuid(), InitialTeamId = null, ProfileKey = "invented", Outcome = "Refined delivery scope",
                    AuthorityEnvelope = new(null, null, [], [], ["launch"], null) }, json), role = "assistant" })); })
            .RegisterCapability<WorkstreamPlanProposalV2Request, MutationResponse>("platform.workstream.plan.propose.v2", (r, _) => {
                submitted.Add(r); return Task.FromResult(new MutationResponse(false, 0, proposalId, "Pending")); });
        var context = runtime.CreateContext(identity: new(producer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Director"));
        var agent = new SpecialistAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), context, default);
        Assert.True(configured.Succeeded, configured.Error);
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 1, 1, "Create project", "Review proposal", [],
            new(producer, Guid.NewGuid(), "Producer", "Producer"), new(manager, Guid.NewGuid(), "Director", "Director"), false,
            [new(Guid.NewGuid(), 0, manager, "Continue", "Prepare proposal", DateTimeOffset.UtcNow,
                new("video-game.project-foundation.request.v1", "1.0", "vision-digest", 1, true, JsonSerializer.SerializeToElement(template, json), "digest"))]);
        var first = await agent.HandleCoordinationTurnAsync(request, context, default);
        var replay = await agent.HandleCoordinationTurnAsync(request, context, default);
        Assert.Equal(1, models); Assert.Equal(2, submitted.Count);
        Assert.Equal(submitted[0].IdempotencyKey, submitted[1].IdempotencyKey);
        Assert.All(submitted, x => { Assert.Equal(producer, x.AccountableManagerOrganizationUserId); Assert.Equal(team, x.InitialTeamId);
            Assert.Equal(template.ProfileKey, x.ProfileKey); Assert.Contains("launch", x.AuthorityEnvelope.HumanRequiredActionKeys); });
        Assert.Equal("Continue", first.Disposition);
        Assert.Equal(proposalId, first.Artifact!.Payload.GetProperty("proposalId").GetGuid());
        Assert.Equal(first.Artifact.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText());
    }
}

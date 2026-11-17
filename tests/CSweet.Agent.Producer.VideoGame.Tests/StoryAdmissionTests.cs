using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_story_review_uses_returned_briefs_and_recovers_retained_refinement(bool resourceChanges)
    {
        var f = new Fixture(adaptive: true);
        var context = f.Runtime.CreateContext(identity: new(f.Session.Initiator.OrganizationUserId.ToString(), "Gabriel",
            null, null, null, [], null, null, null));
        var briefId = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var handoff = new ProducerAcceptedHandoff(f.Cycle.WorkstreamId, briefId, revision, "brief", "handoff",
            Guid.NewGuid(), f.Cycle.ApprovedPackageId, 1, DateTimeOffset.UtcNow);
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var resource = JsonSerializer.Deserialize<ResourceRequestState>("{}")! with {
            Id = Guid.NewGuid(), WorkstreamId = f.Cycle.WorkstreamId, Status = "Certifying", Revision = 1 };
        var prompts = new List<JsonElement>();
        f.Runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
                (_, _) => Task.FromResult(new TeamRosterV2Response(f.Roster, f.Cycle.WorkstreamId)))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([resource]))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(
                new ArtifactDocument(briefId, "Brief", "scope", "Accepted", revision, null, revision, null, null,
                    [new(revision, 1, null, "Scope baseline and guardrails", "brief", "Accepted", DateTimeOffset.UtcNow, null, null)])))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) => {
                var input = JsonSerializer.Deserialize<JsonElement>(r.GetProperty("messages")[1].GetProperty("text").GetString()!);
                prompts.Add(input);
                bool hasBriefs = input.GetProperty("provisional").ValueKind == JsonValueKind.Array;
                var reply = new SpecialistAgent.ReviewChoice("Advance the first Story", 0, true, hasBriefs,
                    "After preparation", "Planning only", [new("proceed", "The scope is current", null, [0])]) { Disposition = "select" };
                return Task.FromResult<object>(new { text = JsonSerializer.Serialize(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            });
        var agent = new SpecialistAgent();
        Assert.True((await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test")
            }))).Succeeded);
        Task<SpecialistAgent.ProgressiveAdvance> Advance(ProgressivePlanningProgress? progress) =>
            SpecialistAgent.AdvanceProgressivePlanningAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Member, f.Session, f.Artifact,
                f.Outline, progress, context, default, (p, choices, board, plans, sprints, draft) =>
                    agent.ReviewAdaptivePlanAsync(f.Cycle, f.Roster, p, choices, board, plans, sprints, f.Evidence, handoff, context, default, draft: draft));

        var kickoff = await Advance(null);
        Assert.Equal(JsonValueKind.Null, Assert.Single(prompts).GetProperty("provisional").ValueKind);
        f.CompleteRefinement("traceability", "non-goals", "authority", "ownership");
        if (resourceChanges) resource = resource with { Status = "Ready", Revision = 2 };
        var admitted = await Advance(kickoff.Progress);
        Assert.Equal(f.Refinement!.Id, admitted.Session?.Id);
        Assert.Equal(2, prompts.Count);
        var input = prompts[1];
        var story = kickoff.Progress.PendingStoryKey!;
        Assert.Equal(story, input.GetProperty("provisionalStory").GetString());
        Assert.Equal(story, input.GetProperty("storyDefinition").GetProperty("ProposalKey").GetString());
        var tasks = f.Detail!.DeliveryItems.Where(i => i.ParentProposalKey == story).ToArray();
        Assert.Equal(6, tasks.Length);
        Assert.Equal(JsonSerializer.Serialize(tasks), input.GetProperty("provisional").GetRawText());

        // Older Producers persisted this shape after setting aside the exact same draft.
        var retained = kickoff.Progress with { PendingStoryKey = null, RefinementSessionId = null,
            DraftStoryKey = story, DraftSessionId = f.Refinement.Id, DraftDirection = f.Evidence.DirectionsDigest,
            DraftBasis = kickoff.Progress.AcceptedBases[story] };
        var resumed = await Advance(JsonSerializer.Deserialize<ProgressivePlanningProgress>(JsonSerializer.Serialize(retained)));
        var recovered = await Advance(resumed.Progress);
        Assert.Equal(f.Refinement.Id, recovered.Session?.Id);
        Assert.Single(f.Starts);
        Assert.Equal(2, prompts.Count); // Reuse the receipt only for these same briefs and current facts.
        // A settled decision on this exact draft survives resource movement that does not bear on it.
        resource = resource with { Status = "Ready", Revision = resource.Revision + 1 };
        var replayed = await Advance(kickoff.Progress);
        Assert.Equal(f.Refinement.Id, replayed.Session?.Id);
        Assert.Equal(2, prompts.Count);
        for (int replay = 0; replay < 2; replay++)
            await SpecialistAgent.PublishCanonicalBacklogAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Refinement, f.DetailArtifact!,
                new(f.Cycle, [], [], [], "accepted"), f.Refinement, f.DetailArtifact!, f.Detail, context, default);
        Assert.Single(f.Items, i => i.Kind == "Story");
        Assert.Equal(6, f.Items.Count(i => i.Kind == "Task"));
    }

    [Fact]
    public void Recorded_Prism_reply_requires_a_revision_reason_only_when_judging_the_selected_draft()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-first-story-review.json"));
        var reply = JsonSerializer.Deserialize<SpecialistAgent.ReviewChoice>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var facts = Enumerable.Range(0, 13).Select(i => new SpecialistAgent.ReviewFact("brief", Guid.Empty, "1", $"Fact {i}")).ToArray();
        SpecialistAgent.ValidateReview(reply, 1, [], facts);
        var error = Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.ValidateReview(reply, 1, [], facts, 0));
        Assert.Contains("draftRevisionReason", error.Message);
        SpecialistAgent.ValidateReview(reply with { KeepDraft = true }, 1, [], facts, 0);
        SpecialistAgent.ValidateReview(reply with { DraftRevisionReason = "Revise the first task to use the newly approved target." }, 1, [], facts, 0);
    }
}

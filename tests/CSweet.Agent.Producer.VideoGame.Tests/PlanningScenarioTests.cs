using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// Scripted journeys from an accepted roadmap to a published Story. Each one replays conditions that stalled
/// a live business between November 15 and 17, 2026: a reply the model got wrong, a hire that landed during
/// review, a scope-ownership gap, and a model that could not produce an acceptable reply at all. The model
/// is scripted, the issue registry refuses what the platform refuses, and everything else is the real Producer.
/// These cover the Producer's planning core. They stop at publication: estimates, capacity, sprint start and
/// execution are separate journeys.
/// </summary>
public sealed partial class ProgressivePlanningTests
{
    private sealed class PlanningJourney
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        public readonly Fixture F = new(adaptive: true, independent: true);
        public readonly IssueRegistry Issues;
        public readonly Queue<string> ScriptedReplies = new();
        public readonly List<JsonElement> Prompts = [];
        public readonly ProducerAcceptedHandoff Handoff;
        public ResourceRequestState Resource;
        private readonly SpecialistAgent agent = new();
        private readonly AgentRuntimeContext context;
        public ProducerPlanningCycleState State;

        public PlanningJourney()
        {
            Issues = new IssueRegistry(F.Runtime);
            var briefId = Guid.NewGuid(); var revision = Guid.NewGuid();
            Handoff = new(F.Cycle.WorkstreamId, briefId, revision, "brief", "handoff", Guid.NewGuid(), F.Cycle.ApprovedPackageId, 1, DateTimeOffset.UtcNow);
            State = new(F.Cycle.WorkstreamId, F.Cycle.BoardId, F.Cycle.TeamId, F.Cycle.PlanningFingerprint, null, null, null, null, F.Artifact.Digest, null, DateTimeOffset.UtcNow);
            Resource = JsonSerializer.Deserialize<ResourceRequestState>("{}")! with { Id = Guid.NewGuid(), WorkstreamId = F.Cycle.WorkstreamId, Status = "Certifying", Revision = 1 };
            var states = new Dictionary<string, AgentOperatingStateResponse>();
            F.Runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
                    (_, _) => Task.FromResult(new TeamRosterV2Response(F.Roster, F.Cycle.WorkstreamId)))
                .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                    (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
                .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
                    (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([Resource]))
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                    (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
                .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(
                    new ArtifactDocument(briefId, "Brief", "scope", "Accepted", revision, null, revision, null, null,
                        [new(revision, 1, null, "A playable Breakout loop", "brief", "Accepted", DateTimeOffset.UtcNow, null, null)])))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
                .RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) => {
                    var messages = r.GetProperty("messages");
                    Prompts.Add(JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("text").GetString()!));
                    if (ScriptedReplies.Count > 0) return Task.FromResult<object>(new { text = ScriptedReplies.Dequeue() });
                    var input = Prompts[^1];
                    var candidates = input.GetProperty("candidates").GetArrayLength();
                    var reply = new SpecialistAgent.ReviewChoice("Advance the next increment", candidates > 0 ? 0 : null, true,
                        input.GetProperty("provisional").ValueKind == JsonValueKind.Array, "After this increment", "Planning only",
                        [new(candidates > 0 ? "proceed" : "refine", "Current evidence supports it", null, [0])]) { Disposition = candidates > 0 ? "select" : "wait" };
                    return Task.FromResult<object>(new { text = JsonSerializer.Serialize(reply, Web) });
                });
            context = F.Runtime.CreateContext(identity: new(F.Session.Initiator.OrganizationUserId.ToString(), "Gabriel", null, null, null, [], null, null, null));
            var configured = F.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("scripted") })).GetAwaiter().GetResult();
            Assert.True(configured.Succeeded);
        }

        public static string Reply(object? nextCandidate, string disposition = "select") => JsonSerializer.Serialize(new {
            disposition, nextGoal = "Start the playable loop", nextCandidate, draftAhead = true, keepDraft = true,
            revisitWhen = "After the first build", feelSummary = "Planning only; nothing is playable yet.",
            actions = new[] { new { kind = "proceed", rationale = "The brief is accepted", capability = (string?)null, evidenceIndexes = new[] { 0 } } },
            additionalDraftCandidates = Array.Empty<int>() }, Web);

        /// <summary>One planning pass: track ownership findings beside the plan, then advance.</summary>
        public async Task<SpecialistAgent.ProgressiveAdvance> PassAsync(SpecialistAgent.ProgressiveAdvance? previous, SpecialistAgent.OwnershipReview ownership)
        {
            ownership = await SpecialistAgent.CarryRecordedResolutionsAsync(ownership, F.Cycle.BoardId, Handoff, context, default);
            State = await SpecialistAgent.ReconcileOwnershipFindingsAsync(F.Cycle, State, F.Evidence, ownership, F.Outline, F.Member, Handoff, context, default);
            return await SpecialistAgent.AdvanceProgressivePlanningAsync(F.Cycle.BoardId, F.Roster, F.Cycle, [], F.Member, F.Session, F.Artifact,
                F.Outline, previous?.Progress, context, default, (p, choices, board, plans, sprints, draft) =>
                    agent.ReviewAdaptivePlanAsync(F.Cycle, F.Roster, p, choices, board, plans, sprints, F.Evidence, Handoff, context, default, ownership, draft), ownership);
        }

        public Task PublishAsync() => SpecialistAgent.PublishCanonicalBacklogAsync(F.Cycle.BoardId, F.Roster, F.Cycle, [], F.Refinement!, F.DetailArtifact!,
            new(F.Cycle, [], [], [], "accepted"), F.Refinement!, F.DetailArtifact!, F.Detail!, context, default);
    }

    [Fact]
    public async Task Journey_a_fresh_roadmap_reaches_a_published_story_through_bad_replies_a_hire_and_an_open_gap()
    {
        var j = new PlanningJourney();
        // November 17: the ownership review reported a missing implementer on the first Epic.
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Engineering capacity for the first increment", "No implementer is named.", ["prototype"]) {
            Impact = "Scoped", ObligationKey = "first-increment-capacity", BriefTitle = "No implementer for the first increment" }]);
        // November 17: the model named the Story by key, then omitted it, then answered correctly.
        j.ScriptedReplies.Enqueue(PlanningJourney.Reply("prototype.story-1"));
        j.ScriptedReplies.Enqueue(PlanningJourney.Reply(null));

        var kickoff = await j.PassAsync(null, ownership);
        Assert.Null(kickoff.Blocker); Assert.Null(kickoff.Retry);
        Assert.Equal("prototype.story-1", kickoff.Progress.PendingStoryKey);
        Assert.Single(j.F.Starts); Assert.Equal(3, j.Prompts.Count);
        Assert.Equal("Open", Assert.Single(j.Issues.Issues).Status); Assert.Equal(1, j.Issues.CommissionCalls);
        Assert.Empty(j.State.OwnershipHeldProposalKeys);

        // The Technical Director returns the Story while a hire and a toolchain change land.
        j.F.CompleteRefinement("paddle-input", "ball-rebound", "brick-removal");
        j.Resource = j.Resource with { Status = "Ready", Revision = 2 };
        var admitted = await j.PassAsync(kickoff, ownership);
        Assert.Null(admitted.Blocker); Assert.Null(admitted.Retry);
        Assert.Equal(j.F.Refinement!.Id, admitted.Session?.Id);

        for (var replay = 0; replay < 2; replay++) await j.PublishAsync();
        Assert.Single(j.F.Items, i => i.Kind == "Story");
        Assert.Equal(5, j.F.Items.Count(i => i.Kind == "Task"));
        // The gap is still open and tracked. It never held the work, and it is commissioned only once.
        Assert.Equal("Open", Assert.Single(j.Issues.Issues).Status); Assert.Equal(1, j.Issues.CommissionCalls);
    }

    [Fact]
    public async Task Journey_a_model_that_cannot_answer_is_retried_later_and_planning_then_continues()
    {
        var j = new PlanningJourney();
        var ownership = new SpecialistAgent.OwnershipReview([new("Playable loop", ["approved"], [new("prototype", "The first Epic owns the loop")]) { ObligationKey = "loop" }], []);
        for (var attempt = 0; attempt < 3; attempt++) j.ScriptedReplies.Enqueue(PlanningJourney.Reply("prototype.story-1"));

        var failed = await j.PassAsync(null, ownership);
        // Not a code defect and not a permanent stop: the runtime waits and tries again within its budget.
        Assert.Null(failed.Blocker); Assert.Null(failed.Waiting); Assert.False(failed.Complete);
        Assert.Contains("nextCandidate", failed.Retry); // The retry reason keeps the exact rejection.
        Assert.Empty(j.F.Starts);

        var retried = await j.PassAsync(failed, ownership);
        Assert.Null(retried.Retry); Assert.Null(retried.Blocker);
        Assert.Equal("prototype.story-1", retried.Progress.PendingStoryKey); Assert.Single(j.F.Starts);
    }

    [Fact]
    public async Task Journey_a_failed_story_refinement_is_asked_again_with_its_reason()
    {
        var j = new PlanningJourney();
        var ownership = new SpecialistAgent.OwnershipReview([new("Playable loop", ["approved"], [new("prototype", "The first Epic owns the loop")]) { ObligationKey = "loop" }], []);
        var kickoff = await j.PassAsync(null, ownership);
        Assert.Equal("prototype.story-1", kickoff.Progress.PendingStoryKey);

        // The Technical Director's model could not produce the Story page.
        const string diagnostic = "Page failed after three attempts: approaching Epic (finish=stop): Assign every approved Epic criterion to a Story.";
        j.F.Refinement = j.F.Refinement! with { Status = "Blocked",
            Turns = [.. j.F.Refinement.Turns, new(Guid.NewGuid(), 1, j.F.Person.OrganizationUserId, "Blocked", diagnostic, DateTimeOffset.UtcNow)] };
        var failed = await j.PassAsync(kickoff, ownership);
        Assert.Null(failed.Blocker); Assert.Contains("Assign every approved Epic criterion", failed.Retry);
        Assert.Equal(1, failed.Progress.StoryRetries); Assert.Null(failed.Progress.PendingStoryKey);

        // The retry requests the same Story under a new key and says what was wrong.
        var retried = await j.PassAsync(failed, ownership);
        Assert.Null(retried.Retry); Assert.Null(retried.Blocker);
        Assert.Equal("prototype.story-1", retried.Progress.PendingStoryKey);
        Assert.Equal(2, j.F.Starts.Count);
        Assert.NotEqual(j.F.Starts[0].IdempotencyKey, j.F.Starts[1].IdempotencyKey);
        Assert.Contains("The previous attempt could not be used", j.F.Starts[1].InitialMessage);
        Assert.Contains("Assign every approved Epic criterion", j.F.Starts[1].InitialMessage);

        // A usable Story clears the retry state.
        j.F.CompleteRefinement();
        var admitted = await j.PassAsync(retried, ownership);
        Assert.Equal(j.F.Refinement!.Id, admitted.Session?.Id);
        Assert.Equal(0, admitted.Progress.StoryRetries); Assert.Null(admitted.Progress.StoryRetryReason);
    }

    [Fact]
    public async Task RecoveredStoryWaitsForItsOriginalDecisionAuthorityAndReusesOneRequestOnReplay()
    {
        var j = new PlanningJourney();
        var manager = Guid.NewGuid();
        var issuer = Guid.NewGuid();
        var context = j.F.Runtime.CreateContext(identity: new(issuer.ToString(), "Producer", null, "Producer", null, [], null, manager.ToString(), "Director"));
        var prior = JsonSerializer.Deserialize<DecisionRecord>("{}")! with {
            Id = Guid.NewGuid(), Revision = 2, WorkstreamId = j.F.Cycle.WorkstreamId, Status = "Decided",
            SelectedOptionId = "choice-0", RequestedByOrganizationUserId = issuer, DecidedByOrganizationUserId = manager,
            TypeKey = "video-game.management-direction.v1", AuthorityRuleKey = "work-planning", Summary = "Hold this Story pending repair",
            Evidence = [new("artifact", j.Handoff.ArtifactId, j.Handoff.AcceptedRevisionId, j.Handoff.RevisionDigest, "video-game.production-plan.v1", "Accepted")] };
        var registry = new SpecialistAgent.ReviewProposals([new("escalate", prior.Id, "Decided", "old-action")]);
        await new RevisionSafeProjectState(context.Platform).MergeAsync<SpecialistAgent.ReviewProposals>(
            "game-review-proposals:" + j.F.Cycle.WorkstreamId.ToString("N"), "game.review-proposals.v1", 1, _ => registry,
            new Dictionary<string,string>(), "seed-prior-action", default);
        var requests = new Dictionary<string, DecisionRecord>();
        j.F.Runtime.RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (request, _) => {
            Assert.Null(request.SupersedesDecisionId);
            requests.TryAdd(request.IdempotencyKey, prior with { Id = Guid.NewGuid(), Status = "Pending", TypeData = request.TypeData,
                SelectedOptionId = null, DecidedByOrganizationUserId = null, Rationale = null });
            return Task.FromResult(requests[request.IdempotencyKey]);
        }).RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (r, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([requests.Values.Single(d => d.Id == r.DecisionId)]));
        await j.F.Review(null); j.F.CompleteRefinement();
        var draft = SpecialistAgent.StoryReviewDraft.FromProposal("prototype.story-1", j.F.Refinement!.Id, j.F.DetailArtifact!.Digest, j.F.Detail!);
        Task<string?> Review() => SpecialistAgent.ReconcileRecoveryDirectionAsync(j.F.Cycle, j.Handoff, draft, [prior], context, default);
        Assert.NotNull(await Review());
        var key = Assert.Single(requests.Keys);
        requests[key] = requests[key] with { Status = "Decided", SelectedOptionId = ContributionAdmission.Hold,
            Rationale = "Still held", DecidedByOrganizationUserId = manager };
        Assert.NotNull(await Review());
        requests[key] = requests[key] with { SelectedOptionId = ContributionAdmission.Admit, Rationale = "Both original briefs now validate" };
        Assert.Null(await Review());
        Assert.Single(requests);
    }

    [Fact]
    public async Task Journey_a_binding_conflict_holds_only_its_own_scope_while_independent_work_is_selected()
    {
        var j = new PlanningJourney();
        var requirements = new PlanningOwnershipEvidence[] { new("one", "Constraint", "Use only provided audio.", ["final"]),
            new("two", "Decision", "Provided audio is forbidden.", ["final"]) };
        var ownership = new SpecialistAgent.OwnershipReview([], [new("Incompatible audio requirements", "Both cannot hold.", ["final"]) {
            ConflictEvidence = requirements, ObligationKey = "audio-conflict" }]);
        j.F.Runtime.RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest,
            (_, _) => Task.FromResult(JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Id = Guid.NewGuid() }));

        var kickoff = await j.PassAsync(null, ownership);
        Assert.Null(kickoff.Blocker); Assert.Null(kickoff.Retry);
        Assert.Equal(new[] { "final" }, j.State.OwnershipHeldProposalKeys);
        Assert.Equal("prototype.story-1", kickoff.Progress.PendingStoryKey);
        var offered = j.Prompts[^1].GetProperty("candidates");
        Assert.Equal(1, offered.GetArrayLength()); // The held Epic is not offered; the independent one is.
    }
}

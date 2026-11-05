using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Fact]
    public async Task Prerequisite_completion_requeues_a_parked_decision_wait_once_across_duplicate_wakes_and_restart()
    {
        var root = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Revision = 10,
            Status = "Blocked", CorrelationId = "producer-planning:retained", BlockReason = SpecialistAgent.PlanningDecisionBlockMarker };
        var states = new Dictionary<string, AgentOperatingStateResponse>(); var requeues = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(
                new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, [root])])))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
                    "Active", new Dictionary<string, string>(), [], "binding", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) => {
                requeues++; Assert.Equal(root.Revision, r.ExpectedRevision);
                return Task.FromResult(root = root with { Status = "Ready", Revision = root.Revision + 1 }); });
        async Task Wake() => await SpecialistAgent.EnsureCommitmentAsync(root.CorrelationId!, "Plan", "Continue", "High", new(),
            runtime.CreateContext(), default, progressiveContinuation: new("completed-pin") { PrerequisiteCompleted = true });
        await Wake(); await Wake(); Assert.Equal(1, requeues);
        root = root with { Status = "Blocked", Revision = 12 };
        await Wake(); Assert.Equal(1, requeues);
    }

    [Fact]
    public async Task Direction_assessment_reuses_exact_graph_and_refined_tasks_without_new_outline_or_publication()
    {
        var f = new Fixture(); var initial = await f.Advance(null); f.CompleteRefinement();
        var refined = await f.Advance(initial.Progress);
        var progress = refined.Progress with { ScheduledArtifactDigest = f.DetailArtifact!.Digest };
        var originalIds = f.Items.Select(x => x.Id).ToArray();
        var evidence = f.Evidence with { Phase = "Direction", ReviewedDirectionsDigest = new string('e', 64), ReviewedAuthorityQuestions = [] };
        var payload = JsonSerializer.SerializeToNode(f.Outline)!.AsObject();
        payload["progressivePlanning"] = JsonSerializer.SerializeToNode(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var artifact = f.Artifact with { Payload = JsonSerializer.SerializeToElement(payload) };
        var result = await SpecialistAgent.AdvanceProgressivePlanningAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Member,
            f.Session, artifact, f.Outline, progress, f.Runtime.CreateContext(), default);
        Assert.Null(result.Blocker); Assert.Equal(f.Refinement!.Id, result.Session!.Id);
        Assert.Empty(result.ReviewedAuthorityQuestions!); Assert.Single(f.Starts); Assert.Equal(originalIds, f.Items.Select(x => x.Id));
        Assert.Equal(f.DetailArtifact.Digest, result.Session.Turns.Last().Artifact!.Digest);
        var replay = await SpecialistAgent.AdvanceProgressivePlanningAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Member,
            f.Session, artifact, f.Outline, progress with { ReviewedDirectionsDigest = evidence.ReviewedDirectionsDigest }, f.Runtime.CreateContext(), default);
        Assert.Null(replay.Blocker); Assert.NotNull(replay.Waiting); Assert.Single(f.Starts);
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("unfinished", false)] [InlineData("active-sprint", false)]
    [InlineData("foreign-proposal", false)] [InlineData("uncompleted-sprint", false)]
    public async Task Completed_prerequisite_wakes_same_increment_without_claiming_aggregate_acceptance(string scenario, bool eligible)
    {
        var f = new Fixture(); var initial = await f.Advance(null); f.CompleteRefinement();
        var refined = await f.Advance(initial.Progress);
        await SpecialistAgent.PublishCanonicalBacklogAsync(f.Cycle.BoardId, f.Roster, f.Cycle, [], f.Session, f.Artifact,
            new(f.Cycle, [], [], [], "designer"), f.Refinement!, f.DetailArtifact!, f.Detail!, f.Runtime.CreateContext(), default);
        var sprint = new WorkSprint(Guid.NewGuid(), f.Cycle.BoardId, "Phase 1", "Prerequisite", "Completed", null, null,
            null, DateTimeOffset.UtcNow, null, 0, 0, 0, 0, 1) { Sequence = 1 };
        var pinIndex = f.Items.FindIndex(x => x.ProposalProvenance?.ProposalItemKey == "implementation");
        f.Items[pinIndex] = f.Items[pinIndex] with { Status = scenario == "unfinished" ? "Running" : "Completed", SprintId = sprint.Id };
        var progress = refined.Progress with { ScheduledArtifactDigest = f.DetailArtifact!.Digest, ScheduledSprintSequence = 1 };
        var cycle = new ProducerPlanningCycleState(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId, f.Cycle.PlanningFingerprint,
            null, null, null, null, scenario == "foreign-proposal" ? "foreign" : f.DetailArtifact.Digest, "reconciled", DateTimeOffset.UtcNow) {
            Progressive = progress, EngineeringPrerequisiteKeys = ["implementation"] };
        f.Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), ProjectStateKeys.Portfolio("producer"), "portfolio", 1,
                "Active", new Dictionary<string, string>(), [], "binding", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new ProducerOperatingState {
                    PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState> { [f.Cycle.WorkstreamId] = cycle } }), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
        IReadOnlyList<WorkSprint> sprints = [sprint with { Status = scenario switch { "active-sprint" => "Active", "uncompleted-sprint" => "Planned", _ => "Completed" } }];
        f.Runtime.RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult(sprints));
        var wake = await SpecialistAgent.ReadProgressiveContinuationAsync(f.Cycle.WorkstreamId, f.Cycle.BoardId,
            f.Cycle.PlanningFingerprint, f.Runtime.CreateContext(), default);
        Assert.Equal(eligible, wake?.PrerequisiteCompleted == true);
        Assert.Equal(eligible, wake?.Fingerprint is not null);
        if (!eligible) return;
        var next = await f.Advance(progress);
        Assert.Null(next.Blocker); Assert.Equal(f.Refinement!.Id, next.Session!.Id); Assert.Single(f.Starts);
        Assert.Equal(progress.ScopeKey, next.Progress.ScopeKey); Assert.Empty(f.Plans);
    }
}

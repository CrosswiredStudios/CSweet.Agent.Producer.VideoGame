using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class SprintProgressionTests
{
    [Fact]
    public async Task Replayed_estimation_waits_for_active_sprint_before_any_planning_effect()
    {
        var board = Guid.NewGuid();
        var commitment = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with
        {
            CorrelationId = "producer-estimation:legacy-premature-successor",
            WorkContext = new(WorkstreamId: Guid.NewGuid(), TeamId: Guid.NewGuid(), BoardId: board, SourceFingerprint: "digest")
        };
        var runtime = new AgentTestRuntime().RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(
            WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([Sprint(board, 1, "Active")]));
        // No roster, estimation, QA or mutation capabilities exist in this runtime.
        var result = await new SpecialistAgent().HandlePersonalTodoAsync(commitment, runtime.CreateContext(), default);
        Assert.Equal(PersonalTodoResult.WaitingUntil(result.NextReviewAt!.Value,
            "Waiting for the active sprint to finish before preparing its successor."), result);
    }

    [Fact]
    public void Completed_sprint_advances_sequence_and_gets_distinct_replay_safe_creation_identity()
    {
        var board = Guid.NewGuid();
        var receipts = new Dictionary<string, WorkSprint>();
        WorkSprint Create(int sequence)
        {
            var key = SpecialistAgent.SprintCreationKey(board, "approved-scope", sequence);
            if (!receipts.TryGetValue(key, out var sprint)) receipts[key] = sprint = Sprint(board, sequence, "Planned");
            return sprint;
        }
        var first = Create(SpecialistAgent.PlanningSprintSequence([]));
        var secondSequence = SpecialistAgent.PlanningSprintSequence([first with { Status = "Completed" }]);
        var second = Create(secondSequence);
        Assert.Equal(2, second.Sequence);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, Create(secondSequence).Id);
        Assert.Equal(2, receipts.Count);
        Assert.Equal(2, SpecialistAgent.PlanningSprintSequence([first with { Status = "Completed" }, second]));
        Assert.Equal(3, SpecialistAgent.PlanningSprintSequence([first with { Status = "Completed" }, second with { Status = "Cancelled" }]));
    }

    [Fact]
    public async Task Reconnect_discovery_ignores_legacy_completed_successor_and_deduplicates_recovery()
    {
        var board = Guid.NewGuid(); var digest = new string('a', 64);
        var legacy = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with
        {
            Id = Guid.NewGuid(), Status = PersonalTodoStatuses.Completed,
            CorrelationId = $"producer-estimation:{board:N}:{digest}:2"
        };
        var items = new List<PersonalTodoItem> { legacy }; var adds = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, items)])))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) =>
            {
                adds++;
                var created = legacy with { Id = Guid.NewGuid(), Status = PersonalTodoStatuses.Ready, CorrelationId = r.CorrelationId };
                items.Add(created); return Task.FromResult(created);
            });
        var correlation = SpecialistAgent.SprintEstimationCorrelation(board, digest, 2);
        Assert.True(SpecialistAgent.CommitmentIdempotencyKey(correlation).Length <= 160);
        var context = runtime.CreateContext();
        var first = await SpecialistAgent.EnsureCommitmentAsync(correlation, "Prepare Sprint 2", "Current scope", "Critical", new(BoardId: board), context, default);
        var replay = await SpecialistAgent.EnsureCommitmentAsync(correlation, "Prepare Sprint 2", "Current scope", "Critical", new(BoardId: board), context, default);
        Assert.NotEqual(legacy.Id, first.Id); Assert.Equal(first.Id, replay.Id); Assert.Equal(1, adds);
        Assert.Equal(PersonalTodoStatuses.Completed, legacy.Status);
    }

    [Theory]
    [InlineData("Completed", false)]
    [InlineData("Done", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Backlog", true)]
    public void Candidate_discovery_excludes_terminal_work_even_after_scope_is_cleared(string status, bool eligible)
    {
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            ExecutionMode = WorkItemExecutionModes.Executable, Status = status,
            ProposalProvenance = new(Guid.NewGuid(), "evidence", "task")
        };
        Assert.Equal(eligible, SpecialistAgent.IsSprintCandidate(item, []));
        Assert.False(SpecialistAgent.IsSprintCandidate(item with { SprintId = Guid.NewGuid() }, []));
    }

    private static WorkSprint Sprint(Guid board, int sequence, string status) =>
        new(Guid.NewGuid(), board, $"Sprint {sequence}", "Deliver", status, null, null, null, null, null, 0, 0, 0, 0, 1) { Sequence = sequence };
}

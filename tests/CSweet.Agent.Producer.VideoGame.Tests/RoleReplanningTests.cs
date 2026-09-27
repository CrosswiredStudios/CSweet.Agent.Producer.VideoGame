using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class RoleReplanningTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    internal static (RoleRepairRequest Request, GameProposedWorkItemV1[] Proposals) Fixture()
    {
        var source = Empty<WorkItem>() with { Id = Guid.NewGuid(), Status = "Blocked", TypeKey = "task",
            ProposalProvenance = new(Guid.NewGuid(), "digest", "plan"),
            Planning = new(["Plan and implement the foundation"], ["Wave template committed", "Working prototype"], ["60 fps"]),
            StageAssignments = [new("specialist-execution", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid())
                { Requirements = new("game-technical-director", [], [], []) }] };
        var downstream = Empty<WorkItem>() with { Id = Guid.NewGuid(), Status = "Assigned", TypeKey = "task",
            ProposalProvenance = new(Guid.NewGuid(), "digest", "game"),
            Planning = new(["Complete game"], ["Playable game"], []) { DependencyItemIds = [source.Id] },
            StageAssignments = [new("specialist-execution", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid())
                { Requirements = new("game-engineer", [], [], []) }] };
        var request = new RoleRepairRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), source.Id,
            ["Working prototype"], ["Move implementation to engineering"], [source, downstream], 2, DateTimeOffset.UtcNow);
        GameProposedWorkItemV1[] proposals = [
            new("plan", "task", "Foundation plan", "Plan", ["Implementation plan documented"], "game-technical-director", [], [], [], []),
            new("prototype", "task", "Build foundation", "Plan and implement the foundation", ["Wave template committed", "Working prototype"], "game-engineer", [], [], [], ["plan"]),
            new("game", "task", "Complete game", "Complete game", ["Playable game"], "game-engineer", [], [], [], ["prototype"])];
        return (request, proposals);
    }

    [Fact]
    public void SplitPreservesRequirementsAndRoutesExecutionThroughTheEngineer()
    {
        var (request, proposals) = Fixture();
        SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, ["60 fps"]);
    }

    [Theory]
    [InlineData("criterion")]
    [InlineData("requirement")]
    [InlineData("constraint")]
    [InlineData("wrong-owner")]
    [InlineData("unrelated-scope")]
    [InlineData("dependency")]
    [InlineData("cycle")]
    [InlineData("missing-key")]
    public void InvalidRepairCannotReplaceTheExistingSprint(string failure)
    {
        var (request, proposals) = Fixture();
        string[] constraints = ["60 fps"];
        switch (failure)
        {
            case "criterion": proposals[1] = proposals[1] with { AcceptanceCriteria = ["Something easier"] }; break;
            case "requirement": proposals[1] = proposals[1] with { Description = "Smaller requirement" }; break;
            case "constraint": constraints = []; break;
            case "wrong-owner": proposals[1] = proposals[1] with { AccountableRoleKey = "game-technical-director" }; break;
            case "unrelated-scope": proposals[2] = proposals[2] with { AcceptanceCriteria = ["Playable game", "Extra scope"] }; break;
            case "dependency": proposals[2] = proposals[2] with { DependencyProposalKeys = ["plan"] }; break;
            case "cycle": proposals[0] = proposals[0] with { DependencyProposalKeys = ["prototype"] }; break;
            case "missing-key": proposals = proposals.Skip(1).ToArray(); break;
        }
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateRoleRepairCoverage(request, proposals, constraints));
    }

    [Theory]
    [InlineData(true, "Working prototype", false)]
    [InlineData(false, "Unknown criterion", false)]
    [InlineData(false, "Working prototype", true)]
    public void ReplanningCannotApproveOrInventUnsatisfiedCriteria(bool approved, string criterion, bool satisfied)
    {
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateAcceptance(new(approved, "Review",
            ["Repair owner"], [new("Working prototype", satisfied, "No implementation")], true, [criterion]), ["Working prototype"]));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("carryover")]
    [InlineData("move")]
    public async Task InterruptedReplanningResumesWithoutDuplicateCancellationOrLosingCompletedWork(string loseResponse)
    {
        var (request, _) = Fixture();
        var source = Empty<WorkSprint>() with { Id = request.SprintId, BoardId = request.BoardId, Revision = 8, Status = "Active" };
        var target = source with { Id = Guid.NewGuid(), Revision = 1, Status = "Planned" };
        var done = request.OriginalItems[1] with { Id = Guid.NewGuid(), Status = "Completed", SprintId = source.Id };
        var items = request.OriginalItems.Select(x => x with { SprintId = source.Id, Revision = 4 }).Append(done).ToList();
        request = request with { OriginalItems = items.ToArray() };
        var stage = Empty<WorkStageExecutionResponse>() with { Id = request.ReviewStageId, Status = "Blocked" };
        var execution = Empty<WorkSprintExecutionResponse>() with { Id = Guid.NewGuid(), BoardId = request.BoardId,
            SprintId = source.Id, Status = "Active", Revision = 12, Items = [Empty<WorkItemExecutionResponse>() with
                { WorkItemId = request.WorkItemId, Stages = [stage] }] };
        var backlog = new WorkBoardColumn(Guid.NewGuid(), "Backlog", "Backlog", 0, "None", null);
        var cancels = 0; var carryovers = 0; var lost = false; var creates = new List<CreateWorkSprintRequest>();
        void Lose(string point) { if (!lost && loseResponse == point) { lost = true; throw new IOException("Lost response"); } }
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Read, (_, _) => Task.FromResult(execution))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(
                new WorkBoardDetail(Empty<WorkBoardSummary>() with { Id = request.BoardId }, [backlog], items.ToArray())))
            .RegisterCapability<ControlWorkSprintExecutionRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Cancel, (control, _) =>
            {
                Assert.Equal(execution.Revision, control.ExpectedSprintRevision);
                cancels++; execution = execution with { Status = "Cancelled", Revision = 13 }; source = source with { Status = "Cancelled", Revision = 9 };
                Lose("cancel"); return Task.FromResult(execution);
            })
            .RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (create, _) =>
            { creates.Add(create); return Task.FromResult(target); })
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([source, target]))
            .RegisterCapability<CarryOverWorkSprintRequest, WorkSprintCarryOver>(WorkSprintCapabilities.CarryOver, (carry, _) =>
            {
                Assert.Equal(source.Revision, carry.ExpectedSourceSprintRevision); carryovers++;
                var moved = items.Where(x => x.Status != "Completed").Select(x => x.Id).ToArray();
                items = items.Select(x => moved.Contains(x.Id) ? x with { SprintId = target.Id, Revision = x.Revision + 1 } : x).ToList();
                Lose("carryover"); return Task.FromResult(new WorkSprintCarryOver(source.Id, target.Id, moved, 0));
            })
            .RegisterCapability<MoveWorkItemRequest, WorkItem>(WorkItemCapabilities.Move, (move, _) =>
            {
                var before = items.Single(x => x.Id == move.ItemId); Assert.Equal(before.Revision, move.ExpectedRevision);
                var after = before with { Status = "Backlog", Revision = before.Revision + 1 };
                items[items.FindIndex(x => x.Id == before.Id)] = after; Lose("move"); return Task.FromResult(after);
            });
        await Assert.ThrowsAnyAsync<Exception>(() => SpecialistAgent.PrepareRoleRepairSprintAsync(request, runtime.CreateContext(), CancellationToken.None));
        await SpecialistAgent.PrepareRoleRepairSprintAsync(request, runtime.CreateContext(), CancellationToken.None);
        Assert.Equal(1, cancels); Assert.Equal(1, carryovers);
        Assert.Single(creates.Distinct());
        Assert.Equal(done, items.Single(x => x.Id == done.Id));
        Assert.All(items.Where(x => x.Id != done.Id), x => { Assert.Equal("Backlog", x.Status); Assert.Equal(target.Id, x.SprintId); });
    }
}

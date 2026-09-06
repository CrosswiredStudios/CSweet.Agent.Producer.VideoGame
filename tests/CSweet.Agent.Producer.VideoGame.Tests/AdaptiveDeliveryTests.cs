using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class AdaptiveDeliveryTests
{
    [Fact]
    public async Task Producer_can_create_one_provisional_sprint_without_any_staffing_or_execution_tools()
    {
        var boardId = Guid.NewGuid();
        var sprints = new List<WorkSprint>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(sprints.ToList()))
            .RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create,
                (request, _) =>
                {
                    var sprint = new WorkSprint(Guid.NewGuid(), boardId, request.Name, request.Goal!, "Planned",
                        request.StartsAt, request.EndsAt, null, null, null, 0, 0, 0, 0, 1) { Sequence = request.Sequence };
                    sprints.Add(sprint);
                    return Task.FromResult(sprint);
                });
        var context = runtime.CreateContext();
        await SpecialistAgent.EnsureDraftSprintAsync(boardId, "accepted-brief", context, CancellationToken.None);
        await SpecialistAgent.EnsureDraftSprintAsync(boardId, "accepted-brief", context, CancellationToken.None);
        var draft = Assert.Single(sprints);
        Assert.Equal("Planned", draft.Status);
        Assert.Null(draft.CapacityPoints);
        Assert.Contains("provisional", draft.Goal);
    }

    [Fact]
    public void Missing_artist_does_not_block_independent_engineering_but_blocks_its_dependents()
    {
        var art = Item(false);
        var gameplay = Item(true);
        var integration = Item(true, art.Id);
        var dependent = Item(true, integration.Id);
        var all = new[] { art, gameplay, integration, dependent };
        Assert.Equal(gameplay.Id, Assert.Single(SpecialistAgent.EligibleCandidateScope(all, all)).Id);
    }

    [Fact]
    public void Completed_dependencies_allow_later_work_without_reentering_scope()
    {
        var prior = Item(false) with { Status = "Done" };
        var next = Item(true, prior.Id);
        Assert.Equal(next.Id, Assert.Single(SpecialistAgent.EligibleCandidateScope([next], [prior, next])).Id);
    }

    [Fact]
    public void A_hired_owner_enables_existing_ticket_and_dependencies_without_duplicates()
    {
        var first = Item(false);
        var next = Item(true, first.Id);
        Assert.Empty(SpecialistAgent.EligibleCandidateScope([first, next], [first, next]));
        var hired = first with { StageAssignments = Item(true).StageAssignments };
        Assert.Equal(new[] { first.Id, next.Id }, SpecialistAgent.EligibleCandidateScope([hired, next], [hired, next]).Select(x => x.Id));
    }

    [Fact]
    public void Unknown_dependency_is_never_treated_as_completed()
    {
        var item = Item(true, Guid.NewGuid());
        Assert.Empty(SpecialistAgent.EligibleCandidateScope([item], [item]));
    }

    private static WorkItem Item(bool assigned, params Guid[] dependencies)
    {
        var installation = Guid.NewGuid();
        return new(Guid.NewGuid(), Guid.NewGuid(), null, null, "Task", "Implement movement", "Deliver movement",
            "Backlog", "High", null, 0, 1, null)
        {
            Planning = new WorkItemPlanningSpecification(["Movement"], ["Input changes position"], []) { DependencyItemIds = dependencies },
            StageAssignments = assigned ? [new WorkStageAssignment("specialist-execution", "AgentInstallation", Guid.NewGuid(), installation)
            {
                Requirements = new("game-engineer", ["gameplay-programming"], [], ["work.execution.run.v1"]),
                SelectionEvidence = new(installation, 1, "profile", ["gameplay-programming"], "selection", DateTimeOffset.UtcNow)
            }] : []
        };
    }
}

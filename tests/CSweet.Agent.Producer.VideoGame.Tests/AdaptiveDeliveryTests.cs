using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class AdaptiveDeliveryTests
{
    [Fact]
    public async Task AttentionReviewUsesDurableHandoffWithoutSupervisionAssignment()
    {
        var stream = Guid.NewGuid();
        var state = new ProducerOperatingState { AcceptedHandoffs = new Dictionary<Guid, ProducerAcceptedHandoff>
        {
            [stream] = new(stream, Guid.NewGuid(), Guid.NewGuid(), "accepted", "handoff", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow)
        }};
        var reads = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(new AgentOperatingStateResponse(
                    Guid.NewGuid(), request.StateKey, "test", 1, "Active", new Dictionary<string, string>(), [], request.StateKey, [], Guid.NewGuid(),
                    System.Text.Json.JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
            .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1,
                (request, _) => { reads++; Assert.Equal(stream, Assert.Single(request.WorkstreamIds!)); return Task.FromResult(new PortfolioResponse([])); });
        await new SpecialistAgent().HandleAttentionReviewAsync(new AgentAttentionReviewContext(Guid.NewGuid(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "test"), runtime.CreateContext(), CancellationToken.None);
        Assert.Equal(1, reads);
    }

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

    [Fact]
    public void ReviewAndQaAssignmentsDoNotHideTheImplementationOwner()
    {
        var item = Item(true);
        var implementation = item.StageAssignments[0];
        var review = implementation with { StageKey = "technical-review", Requirements = implementation.Requirements! with { RequiredRoleKey = "game-technical-director" } };
        var qa = implementation with { StageKey = "quality", Requirements = implementation.Requirements! with { RequiredRoleKey = "game-quality-assurance" } };
        item = item with { StageAssignments = [review, qa, implementation] };
        Assert.Same(implementation, SpecialistAgent.PrimaryExecutionAssignment(item));
        Assert.Equal(item.Id, Assert.Single(SpecialistAgent.EligibleCandidateScope([item], [item])).Id);
    }

    [Fact]
    public void MissingQaDelegationPreventsSprintSelectionUntilItIsStaffed()
    {
        var item = Item(true);
        var qaPlan = new WorkTechnicalDelegationRecommendation("quality", "game-quality-assurance",
            ["work.execution.run.v1"], null, true, "Validate the candidate independently");
        item = item with { Planning = item.Planning! with { DelegationRecommendations = [qaPlan] } };
        Assert.Equal("quality", Assert.Single(SpecialistAgent.MissingDelegations(item)).StageKey);
        Assert.Empty(SpecialistAgent.EligibleCandidateScope([item], [item]));
        var qa = item.StageAssignments[0] with { StageKey = "quality", Requirements = item.StageAssignments[0].Requirements! with { RequiredRoleKey = "game-quality-assurance" } };
        var staffed = item with { StageAssignments = [..item.StageAssignments, qa] };
        Assert.Empty(SpecialistAgent.MissingDelegations(staffed));
        Assert.Equal(staffed.Id, Assert.Single(SpecialistAgent.EligibleCandidateScope([staffed], [staffed])).Id);
        Assert.Same(item.StageAssignments[0], SpecialistAgent.PrimaryExecutionAssignment(staffed));
    }

    [Fact]
    public void ReviewOnlyAndDuplicateImplementationAssignmentsAreNotExecutableCandidates()
    {
        var item = Item(true);
        var reviewOnly = item with { StageAssignments = [item.StageAssignments[0] with { StageKey = "technical-review" }] };
        var duplicate = item with { StageAssignments = [item.StageAssignments[0], item.StageAssignments[0]] };
        Assert.Empty(SpecialistAgent.EligibleCandidateScope([reviewOnly, duplicate], [reviewOnly, duplicate]));
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

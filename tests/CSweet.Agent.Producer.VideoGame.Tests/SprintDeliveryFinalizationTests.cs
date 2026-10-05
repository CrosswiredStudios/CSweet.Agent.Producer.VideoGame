using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class SprintDeliveryFinalizationTests
{
    [Fact]
    public async Task Ready_planning_is_finalized_before_preflight_and_restart_does_not_duplicate_delivery()
    {
        var fixture = new Journey { PreflightValid = false };
        var result = await new SpecialistAgent().HandlePersonalTodoAsync(fixture.Commitment, fixture.Context, default);
        Assert.Equal(2, fixture.Finalizations.Count);
        Assert.Equal(0, fixture.Starts);
        Assert.All(fixture.Finalizations, request =>
        {
            var original = fixture.Original[request.ItemId];
            Assert.Equal(original.Planning!.Requirements, request.Delivery.Requirements);
            Assert.Equal(original.Planning.AcceptanceCriteria, request.Delivery.AcceptanceCriteria);
            Assert.Equal(original.Planning.Constraints, request.Delivery.Constraints);
            Assert.Equal(original.Planning.DependencyItemIds, request.Delivery.DependencyItemIds);
            Assert.Equal(original.StageAssignments, request.StageAssignments);
            Assert.Equal(original.AccountableOrganizationUserId, request.AccountableOrganizationUserId);
            Assert.Equal(original.Revision, request.ExpectedRevision);
            Assert.Equal(fixture.RepositoryId, request.Delivery.RepositoryId);
            Assert.Equal("main", request.Delivery.BaseBranch);
        });
        Assert.Null(fixture.Items[fixture.UnscheduledId].Delivery);
        Assert.Null(fixture.Items[fixture.ContainerId].Delivery);
        fixture.PreflightValid = true;
        result = await new SpecialistAgent().HandlePersonalTodoAsync(fixture.Commitment, fixture.Context, default);
        Assert.Equal(PersonalTodoResult.Completed($"Started sprint {fixture.Sprint.Name} after authoritative preflight passed."), result);
        Assert.Equal(2, fixture.Finalizations.Count);
        Assert.Equal(1, fixture.Starts);
        await new SpecialistAgent().HandlePersonalTodoAsync(fixture.Commitment, fixture.Context, default);
        Assert.Equal(1, fixture.Starts);
    }

    [Theory]
    [InlineData("no-repository")]
    [InlineData("ambiguous-repository")]
    [InlineData("no-branch")]
    [InlineData("wrong-project")]
    [InlineData("wrong-team")]
    [InlineData("missing-owner")]
    [InlineData("missing-assignment")]
    [InlineData("missing-planning")]
    public async Task Missing_authoritative_prerequisites_do_not_finalize_or_start(string condition)
    {
        var fixture = new Journey();
        switch (condition)
        {
            case "no-repository": fixture.Repositories = []; break;
            case "ambiguous-repository": fixture.Repositories = [fixture.Repositories[0], fixture.Repositories[0] with { RepositoryId = Guid.NewGuid() }]; break;
            case "no-branch": fixture.Repositories = [fixture.Repositories[0] with { DefaultBranch = "" }]; break;
            case "wrong-project": fixture.Commitment = fixture.Commitment with { WorkContext = fixture.Commitment.WorkContext! with { WorkstreamId = Guid.NewGuid() } }; break;
            case "wrong-team": fixture.Commitment = fixture.Commitment with { WorkContext = fixture.Commitment.WorkContext! with { TeamId = Guid.NewGuid() } }; break;
            default:
                var ticket = fixture.Items.Values.First(x => x.SprintId == fixture.Sprint.Id && x.ExecutionMode == WorkItemExecutionModes.Executable);
                fixture.Items[ticket.Id] = condition switch
                {
                    "missing-owner" => ticket with { AccountableOrganizationUserId = null },
                    "missing-assignment" => ticket with { StageAssignments = [] },
                    _ => ticket with { Planning = null }
                };
                break;
        }
        await new SpecialistAgent().HandlePersonalTodoAsync(fixture.Commitment, fixture.Context, default);
        Assert.Empty(fixture.Finalizations);
        Assert.Equal(0, fixture.Starts);
    }

    [Fact]
    public async Task Partial_delivery_reuses_the_bound_repository_among_multiple_approved_options()
    {
        var fixture = new Journey();
        var ticket = fixture.Items.Values.First(x => x.SprintId == fixture.Sprint.Id && x.ExecutionMode == WorkItemExecutionModes.Executable);
        fixture.Items[ticket.Id] = ticket with { Delivery = new(fixture.RepositoryId, ticket.Planning!.Requirements, ticket.Planning.AcceptanceCriteria) { BaseBranch = "main" } };
        fixture.Repositories = [fixture.Repositories[0] with { RepositoryId = Guid.NewGuid() }, fixture.Repositories[0]];
        var result = await new SpecialistAgent().HandlePersonalTodoAsync(fixture.Commitment, fixture.Context, default);
        Assert.Equal(PersonalTodoResult.Completed($"Started sprint {fixture.Sprint.Name} after authoritative preflight passed."), result);
        Assert.Equal(fixture.RepositoryId, Assert.Single(fixture.Finalizations).Delivery.RepositoryId);
        Assert.Equal(1, fixture.Starts);
    }

    private sealed class Journey
    {
        private readonly Guid _boardId = Guid.NewGuid(), _projectId = Guid.NewGuid(), _teamId = Guid.NewGuid(), _ownerId = Guid.NewGuid();
        public Guid RepositoryId { get; } = Guid.NewGuid();
        public Guid UnscheduledId { get; private set; }
        public Guid ContainerId { get; private set; }
        public AgentRuntimeContext Context { get; }
        public PersonalTodoItem Commitment { get; set; }
        public WorkSprint Sprint { get; private set; }
        public Dictionary<Guid, WorkItem> Items { get; } = [];
        public Dictionary<Guid, WorkItem> Original { get; }
        public List<FinalizeWorkItemDeliveryRequest> Finalizations { get; } = [];
        public IReadOnlyList<TeamRepositoryOption> Repositories { get; set; }
        public bool PreflightValid { get; set; } = true;
        public int Starts { get; private set; }

        public Journey()
        {
            Sprint = new(Guid.NewGuid(), _boardId, "Production Sprint 1", "Playable demo", "Planned", null, null, null, null, null, 0, 0, 0, 0, 2) { CapacityPoints = 10 };
            Commitment = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with
            {
                CorrelationId = "producer-readiness:" + Sprint.Id.ToString("N"),
                WorkContext = new(WorkstreamId: _projectId, TeamId: _teamId, BoardId: _boardId, SprintId: Sprint.Id)
            };
            WorkItem Ticket(Guid? sprint) => new(Guid.NewGuid(), Guid.NewGuid(), null, sprint, "Task", "Approved task", "", "Ready", "High", 3, 1, 5, null)
            {
                PlanningRevision = 2,
                Planning = new(["Approved requirement"], ["Exact acceptance"], ["Preserve scope"]) { DependencyItemIds = [Guid.NewGuid()] },
                AccountableOrganizationUserId = _ownerId,
                StageAssignments = [new("specialist-execution", "AgentInstallation", _ownerId, Guid.NewGuid())]
            };
            var first = Ticket(Sprint.Id); var second = Ticket(Sprint.Id); var unscheduled = Ticket(null);
            var container = Ticket(Sprint.Id) with { ExecutionMode = WorkItemExecutionModes.Container };
            foreach (var ticket in new[] { first, second, unscheduled, container }) Items.Add(ticket.Id, ticket);
            UnscheduledId = unscheduled.Id; ContainerId = container.Id; Original = new(Items);
            Repositories = [new(RepositoryId, "Project repository", "InternalGit", "internal/game", "main", "PullRequest")];
            var runtime = new AgentTestRuntime()
                .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([Sprint]))
                .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(new WorkBoardDetail(
                    new(_boardId, "GAME", "Game board", false, false, 1, []) { WorkstreamId = _projectId, TeamId = _teamId }, [], Items.Values.ToArray())))
                .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions, (request, _) =>
                {
                    Assert.Equal(_teamId, request.TeamId); return Task.FromResult(Repositories);
                })
                .RegisterCapability<FinalizeWorkItemDeliveryRequest, WorkItem>(WorkItemCapabilities.FinalizeDelivery, (request, _) =>
                {
                    Finalizations.Add(request);
                    var ticket = Items[request.ItemId];
                    Assert.Equal(ticket.Revision, request.ExpectedRevision);
                    return Task.FromResult(Items[ticket.Id] = ticket with { Delivery = request.Delivery, Revision = ticket.Revision + 1 });
                })
                .RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintPreflightResult>(WorkOrchestrationCapabilities.Preflight, (_, _) =>
                {
                    Assert.All(Items.Values.Where(x => x.SprintId == Sprint.Id && x.ExecutionMode == WorkItemExecutionModes.Executable), x => Assert.NotNull(x.Delivery));
                    return Task.FromResult(new WorkSprintPreflightResult(PreflightValid, _boardId, Sprint.Id, null,
                        PreflightValid ? [] : [new("approval.pending", "An approval is still pending.")]));
                })
                .RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Start, (_, _) =>
                {
                    Starts++; Sprint = Sprint with { Status = "Active" };
                    return Task.FromResult(new WorkSprintExecutionResponse(Guid.NewGuid(), _boardId, Sprint.Id, Guid.NewGuid(), _ownerId, "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, []));
                });
            Context = runtime.CreateContext();
        }
    }
}

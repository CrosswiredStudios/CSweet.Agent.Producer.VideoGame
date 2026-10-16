using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class QaReportPlanningTests
{
    [Theory]
    [InlineData("game-quality-assurance", "Artifact")]
    [InlineData("game-engineer", "Code")]
    public void New_proposal_records_the_delivery_kind(string role, string kind) =>
        Assert.Equal(kind, SpecialistAgent.ProposalDeliveryKind(role));

    [Theory]
    [InlineData("Running")]
    [InlineData("InProgress")]
    [InlineData("Done")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public void Started_and_terminal_tasks_are_not_reclassified(string status) =>
        Assert.False(SpecialistAgent.NeedsQaReportPlanningRepair(Ticket() with { Status = status }));

    [Fact]
    public void Code_authors_containers_conflicting_roles_and_bound_deliveries_are_preserved()
    {
        var ticket = Ticket();
        Assert.False(SpecialistAgent.NeedsQaReportPlanningRepair(ticket with { ExecutionMode = WorkItemExecutionModes.Container }));
        Assert.False(SpecialistAgent.NeedsQaReportPlanningRepair(ticket with { Delivery = new(Guid.NewGuid(), [], []) }));
        Assert.False(SpecialistAgent.NeedsQaReportPlanningRepair(ticket with { Planning = ticket.Planning! with {
            DelegationRecommendations = [new("specialist-execution", "game-engineer", [], null, true, "Implementation")] } }));
        Assert.False(SpecialistAgent.NeedsQaReportPlanningRepair(ticket with { StageAssignments = [new("specialist-execution", "AgentInstallation") {
            Requirements = new("game-engineer", [], [], []) }] }));
    }

    [Theory]
    [InlineData("Planned", false, 1)]
    [InlineData("Active", false, 0)]
    [InlineData("Paused", false, 0)]
    [InlineData("Completed", false, 0)]
    [InlineData("Planned", true, 0)]
    public async Task Recovery_preserves_evidence_and_ownership_and_does_not_repeat_or_rewrite_execution(string status, bool historicalExecution, int mutations)
    {
        var manager = Guid.NewGuid(); var boardId = Guid.NewGuid(); var sprintId = Guid.NewGuid();
        var board = new WorkBoardSummary(boardId, "GAME", "Game", false, false, 1, []) {
            ManagerOrganizationUserId = manager, WorkstreamId = Guid.NewGuid() };
        var ticket = Ticket() with { SprintId = sprintId };
        var original = ticket;
        var revisions = new List<ReviseWorkItemPlanningRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>(WorkItemCapabilities.Read,
                (request, ct) => Task.FromResult<object>(request.TryGetProperty("itemId", out _) ? ticket : new WorkBoardDetail(board, [], [ticket])))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([new(sprintId, boardId, "Sprint", "Goal", status, null, null, null, null, null, 0, 0, 0, 0, 1)]))
            .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse?>(WorkOrchestrationCapabilities.Read,
                (_, _) => Task.FromResult(historicalExecution ? new WorkSprintExecutionResponse(Guid.NewGuid(), boardId, sprintId,
                    Guid.NewGuid(), manager, "Cancelled", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, []) : null))
            .RegisterCapability<ReviseWorkItemPlanningRequest, WorkItem>(WorkItemCapabilities.RevisePlanning, (request, _) => {
                revisions.Add(request);
                ticket = ticket with { Planning = request.Planning, Revision = ticket.Revision + 1, PlanningRevision = ticket.PlanningRevision + 1 };
                return Task.FromResult(ticket);
            });
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Producer", null, "Producer", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        await SpecialistAgent.RepairQaReportPlanningAsync(board, context, default);
        await SpecialistAgent.RepairQaReportPlanningAsync(board, context, default);
        Assert.Equal(mutations, revisions.Count);
        if (mutations == 0) { Assert.Equal(original, ticket); return; }
        var revision = Assert.Single(revisions);
        Assert.Equal(JsonSerializer.Serialize(original.Planning! with { DeliveryKind = "Artifact" }), JsonSerializer.Serialize(revision.Planning));
        Assert.Equal(original.Revision, revision.ExpectedRevision);
        Assert.Equal(original.PlanningRevision, revision.ExpectedPlanningRevision);
        Assert.Equal(original.ProposalProvenance, revision.ProposalProvenance);
        Assert.Equal(original.AccountableOrganizationUserId, revision.AccountableOrganizationUserId);
        Assert.Equal(JsonSerializer.Serialize(original.StageAssignments), JsonSerializer.Serialize(revision.StageAssignments));
        Assert.Equal(original.ParentItemId, revision.ParentItemId);
    }

    [Fact]
    public async Task Single_Qa_report_author_receives_independent_manager_review_and_can_review_aggregate_evidence()
    {
        var manager = Guid.NewGuid(); var boardId = Guid.NewGuid(); var project = Guid.NewGuid();
        AgentTeammate Member(string role, Guid? employee = null) => new((employee ?? Guid.NewGuid()).ToString(), role, "Agent", role, role, "Peer", "Online") {
            AgentInstallationId = Guid.NewGuid(), RuntimeEligibility = "Eligible", IsAvailable = true,
            DeclaredRoleKeys = [role], EffectiveCapabilities = [WorkManagementCapabilityNames.ExecutionRun] };
        var producer = Member("manager", manager); var qa = Member("game-quality-assurance"); var td = Member("game-technical-director");
        var roster = new AgentTeamContext(Guid.NewGuid().ToString(), "Game", "Game", 1, manager.ToString(), "Producer", [producer, qa, td], [], 3, false);
        var board = new WorkBoardSummary(boardId, "GAME", "Game", false, false, 1, []) {
            ManagerOrganizationUserId = manager, WorkstreamId = project, TeamId = Guid.Parse(roster.TeamId) };
        var epic = Ticket() with { Kind = "Epic", ExecutionMode = WorkItemExecutionModes.Container, ParentItemId = null };
        var story = Ticket() with { Kind = "Story", ExecutionMode = WorkItemExecutionModes.Container, ParentItemId = epic.Id };
        var report = Ticket() with { ParentItemId = story.Id, StageAssignments = [] };
        var items = new Dictionary<Guid, WorkItem> { [epic.Id] = epic, [story.Id] = story, [report.Id] = report };
        WorkDeliveryPlanResponse? plan = null;
        ConfigureWorkDeliveryPlanRequest? configuration = null;
        var finalizations = new List<FinalizeWorkItemDeliveryRequest>();
        var policy = WorkflowStaffingTests.Policy();
        policy = policy with { Stages = [.. policy.Stages, new("task-integration", "Integrate", "PlatformAction", null, "", "{}", "{}", 60, 1, new())] };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<ConfigureProfileOrchestrationRequest, ConfigureProfileOrchestrationResponse>(WorkOrchestrationCapabilities.ConfigureProfile,
                (_, _) => Task.FromResult(new ConfigureProfileOrchestrationResponse(project, boardId, 1, policy, [])))
            .RegisterCapability<JsonElement, object>(WorkItemCapabilities.Read, (request, ct) => Task.FromResult<object>(
                request.TryGetProperty("itemId", out var id) ? items[id.GetGuid()] : new WorkBoardDetail(board, [], items.Values.ToArray())))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([]))
            .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([]))
            .RegisterCapability<ReviseWorkItemPlanningRequest, WorkItem>(WorkItemCapabilities.RevisePlanning, (request, _) => {
                items[request.ItemId] = items[request.ItemId] with { Planning = request.Planning, Revision = request.ExpectedRevision + 1 };
                return Task.FromResult(items[request.ItemId]);
            })
            .RegisterCapability<ReadWorkDeliveryPlansRequest, IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>(plan is null ? [] : [plan]))
            .RegisterCapability<ConfigureWorkDeliveryPlanRequest, WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Configure, (request, _) => {
                configuration = request;
                plan = new(Guid.NewGuid(), project, request.Name, manager, "Draft", 1, 1, request.EpicItemIds,
                    request.Branches, [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return Task.FromResult(plan);
            })
            .RegisterCapability<FinalizeWorkItemDeliveryRequest, WorkItem>(WorkItemCapabilities.FinalizeDelivery, (request, _) => {
                finalizations.Add(request);
                items[request.ItemId] = items[request.ItemId] with { Delivery = request.Delivery, StageAssignments = request.StageAssignments };
                return Task.FromResult(items[request.ItemId]);
            })
            .RegisterCapability<ControlWorkDeliveryPlanRequest, WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Control, (_, _) => {
                plan = plan! with { Status = "Active" }; return Task.FromResult(plan);
            });
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Producer", null, "Producer", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        Assert.Null(await SpecialistAgent.StaffWorkflowAsync(board, roster, new string('a', 64), context, default));
        var delivered = Assert.Single(finalizations);
        Assert.Equal("Artifact", items[report.Id].Planning!.DeliveryKind);
        Assert.Equal("Artifact", delivered.Delivery.DeliveryKind);
        Assert.Equal(qa.AgentInstallationId, delivered.StageAssignments.Single(x => x.StageKey == "specialist-execution").AgentInstallationId);
        Assert.Equal(producer.AgentInstallationId, delivered.StageAssignments.Single(x => x.StageKey == "quality").AgentInstallationId);
        Assert.All(configuration!.Assignments.Where(x => x.Scope is "Story" or "Release"), scope =>
            Assert.Equal(qa.AgentInstallationId, scope.Stages.Single(x => x.StageKey == "quality").AgentInstallationId));
        Assert.Equal("Active", plan!.Status);
        Assert.Null(await SpecialistAgent.StaffWorkflowAsync(board, roster, new string('a', 64), context, default));
        Assert.Single(finalizations);
    }

    private static WorkItem Ticket() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
        "Task", "QA report", "Validate the product and record findings", "Ready", "High", 5, 0, 3, null) {
        PlanningRevision = 2, AccountableOrganizationUserId = Guid.NewGuid(),
        ProposalProvenance = new(Guid.NewGuid(), new string('a', 64), "qa-report"),
        Planning = new(["Keep exact scope"], ["Report every criterion"], ["Independent review required"]) {
            DependencyItemIds = [Guid.NewGuid()], DelegationRecommendations = [new("specialist-execution", "game-quality-assurance", [], null, true, "Validate")] },
        StageAssignments = [new("specialist-execution", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid()) {
            Requirements = new("game-quality-assurance", [], [], []) }]
    };
}

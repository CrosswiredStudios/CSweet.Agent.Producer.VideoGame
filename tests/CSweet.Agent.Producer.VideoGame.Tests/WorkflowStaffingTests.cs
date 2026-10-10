using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class WorkflowStaffingTests
{
    [Fact]
    public void Implementation_is_selected_before_review_even_when_legacy_assignments_list_review_first()
    {
        var author = Member("game-engineer") with { DeclaredRoleKeys = ["game-engineer", "game-technical-director"] };
        var reviewer = Member("game-technical-director");
        var qa = Member("game-quality-assurance");
        var ticket = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            StageAssignments = [new("technical-review", "AgentInstallation", Guid.Parse(author.EmployeeId), author.AgentInstallationId)
                { Requirements = new("game-technical-director", [], [], ["work.execution.run.v1"]) }]
        };
        var roster = new AgentTeamContext(Guid.NewGuid().ToString(), "Game", "Game", 1, "", "", [author, reviewer, qa], [], 3, false);
        var assignments = SpecialistAgent.RefreshAssignments(ticket, roster, "profile", SpecialistAgent.WorkflowRequirements(Policy()));
        Assert.Equal(author.AgentInstallationId, assignments.Single(a => a.StageKey == "specialist-execution").AgentInstallationId);
        Assert.Equal(reviewer.AgentInstallationId, assignments.Single(a => a.StageKey == "technical-review").AgentInstallationId);
        Assert.Equal(reviewer.AgentInstallationId, assignments.Single(a => a.StageKey == "merge-decision").AgentInstallationId);
    }

    [Fact]
    public async Task Published_development_recovers_missing_review_without_repeating_implementation()
    {
        var developer = Member("game-engineer");
        var reviewer = Member("game-technical-director");
        var qa = Member("game-quality-assurance");
        var manager = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid(); var boardId = Guid.NewGuid();
        var sprintId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var board = new WorkBoardSummary(boardId, "GAME", "Game", false, false, 1, [])
            { TeamId = team, WorkstreamId = project, ManagerOrganizationUserId = manager };
        var policy = Policy();
        var dev = new WorkStageAssignment("specialist-execution", "AgentInstallation", Guid.Parse(developer.EmployeeId), developer.AgentInstallationId)
        {
            Requirements = new("game-engineer", [], [], ["work.execution.run.v1"]),
            SelectionEvidence = new(developer.AgentInstallationId!.Value, 1, "profile", [], "original", now)
        };
        var ticket = new WorkItem(Guid.NewGuid(), boardId, null, sprintId, "Task", "Spike", "Approved scope", "Blocked", "High", null, 0, 7, null)
        {
            AccountableOrganizationUserId = Guid.Parse(developer.EmployeeId), StageAssignments = [dev],
            Planning = new(["Spike"], ["Measured performance"], []),
            Delivery = new(Guid.NewGuid(), ["Spike"], ["Measured performance"]) { BaseBranch = "main" }
        };
        var stages = new[]
        {
            Stage("specialist-execution", "Completed", 1) with { AgentInstallationId = developer.AgentInstallationId, LastOutcomeCode = "code-published" },
            Stage("technical-review", "Blocked", 0) with { PrincipalKind = "Unassigned", LastError = "staffing.assignment_missing" }
        };
        var itemExecution = new WorkItemExecutionResponse(Guid.NewGuid(), ticket.Id, "GAME-1", "technical-review", 0, "Blocked", "staffing.assignment_missing", stages, now);
        var execution = new WorkSprintExecutionResponse(Guid.NewGuid(), boardId, sprintId, policy.RevisionId, manager, "Active", 1, now, now, null, [itemExecution]);
        var finalizations = new List<FinalizeWorkItemDeliveryRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ConfigureProfileOrchestrationRequest, ConfigureProfileOrchestrationResponse>(WorkOrchestrationCapabilities.ConfigureProfile,
                (_, _) => Task.FromResult(new ConfigureProfileOrchestrationResponse(project, boardId, 1, policy, [])))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [ticket])))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([new(sprintId, boardId, "Sprint", "Goal", "Active", null, null, null, null, null, 0, 0, 0, 0, 1)]))
            .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse?>(WorkOrchestrationCapabilities.Read,
                (_, _) => Task.FromResult<WorkSprintExecutionResponse?>(execution))
            .RegisterCapability<FinalizeWorkItemDeliveryRequest, WorkItem>(WorkItemCapabilities.FinalizeDelivery, (request, _) =>
            {
                finalizations.Add(request);
                ticket = ticket with { StageAssignments = request.StageAssignments, Revision = ticket.Revision + 1 };
                execution = execution with { Items = [itemExecution with { Status = "Pending", Stages = [stages[0], stages[1] with { Status = "Pending", LastError = null }] }] };
                return Task.FromResult(ticket);
            });
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Producer", null, "Producer", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        var roster = new AgentTeamContext(team.ToString(), "Game", "Game", 2, "", "", [developer, reviewer, qa], [], 3, false);
        Assert.Null(await SpecialistAgent.StaffWorkflowAsync(board, roster, "profile", context, default));
        var repair = Assert.Single(finalizations);
        Assert.Equal(4, repair.StageAssignments.Count);
        Assert.Equal(JsonSerializer.Serialize(dev), JsonSerializer.Serialize(repair.StageAssignments.Single(a => a.StageKey == "specialist-execution")));
        Assert.Equal(JsonSerializer.Serialize(ticket.Delivery), JsonSerializer.Serialize(repair.Delivery));
        Assert.Equal(reviewer.AgentInstallationId, repair.StageAssignments.Single(a => a.StageKey == "technical-review").AgentInstallationId);
        Assert.Equal(qa.AgentInstallationId, repair.StageAssignments.Single(a => a.StageKey == "quality").AgentInstallationId);
        Assert.Null(await SpecialistAgent.StaffWorkflowAsync(board, roster, "profile", context, default));
        Assert.Single(finalizations);
        var replacement = Member("game-technical-director");
        Assert.Null(await SpecialistAgent.StaffWorkflowAsync(board,
            roster with { Revision = 3, Members = [developer, replacement, qa] }, "profile", context, default));
        Assert.Equal(2, finalizations.Count);
        Assert.Equal(replacement.AgentInstallationId, finalizations[1].StageAssignments.Single(a => a.StageKey == "technical-review").AgentInstallationId);
        Assert.Equal("original", finalizations[1].StageAssignments.Single(a => a.StageKey == "specialist-execution").SelectionEvidence!.DecisionFingerprint);
    }

    [Theory]
    [InlineData("Running", 1)]
    [InlineData("Dispatching", 0)]
    [InlineData("Completed", 1)]
    [InlineData("Blocked", 1)]
    public void Team_changes_preserve_started_assignments(string status, int attempts)
    {
        var original = new WorkStageAssignment("specialist-execution", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid());
        var ticket = JsonSerializer.Deserialize<WorkItem>("{}")! with { StageAssignments = [original] };
        var execution = new WorkItemExecutionResponse(Guid.NewGuid(), ticket.Id, "GAME-1", "specialist-execution", 0, status, null,
            [Stage("specialist-execution", status, attempts)], DateTimeOffset.UtcNow);
        Assert.Same(original, Assert.Single(SpecialistAgent.PreserveStartedAssignments(ticket,
            [original with { AgentInstallationId = Guid.NewGuid() }], execution)));
    }

    [Fact]
    public async Task Hierarchical_staffing_gap_returns_actionable_blocker_instead_of_crashing()
    {
        var manager = Guid.NewGuid(); var project = Guid.NewGuid(); var boardId = Guid.NewGuid();
        var board = new WorkBoardSummary(boardId, "GAME", "Game", false, false, 1, [])
            { TeamId = Guid.NewGuid(), WorkstreamId = project, ManagerOrganizationUserId = manager };
        var policy = Policy();
        policy = policy with { Stages = [.. policy.Stages,
            new("task-integration", "Integration", "PlatformAction", null, "", "{}", "{}", 60, 1, new())] };
        var epic = new WorkItem(Guid.NewGuid(), boardId, null, null, "Epic", "Epic", "", "Ready", "High", null, 0, 1, null)
            { ExecutionMode = WorkItemExecutionModes.Container, Planning = new(["Scope"], ["Accepted"]) };
        var story = epic with { Id = Guid.NewGuid(), ParentItemId = epic.Id, Kind = "Story" };
        var task = story with { Id = Guid.NewGuid(), ParentItemId = story.Id, Kind = "Task", Title = "Product spike",
            ExecutionMode = WorkItemExecutionModes.Executable, Planning = new(["Scope"], ["Accepted"]) { DeliveryKind = "Artifact" } };
        var developer = Member("game-engineer") with { EffectiveCapabilities = [WorkManagementCapabilityNames.ExecutionRunV2], IsAvailable = true };
        var roster = new AgentTeamContext(Guid.NewGuid().ToString(), "Game", "Game", 1, manager.ToString(), "Producer", [developer], [], 1, false);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ConfigureProfileOrchestrationRequest, ConfigureProfileOrchestrationResponse>(WorkOrchestrationCapabilities.ConfigureProfile,
                (_, _) => Task.FromResult(new ConfigureProfileOrchestrationResponse(project, boardId, 1, policy, [])))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [epic, story, task])))
            .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([]))
            .RegisterCapability<ReadWorkDeliveryPlansRequest, IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>([]));
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Producer", null, "Producer", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        var gap = await SpecialistAgent.StaffWorkflowAsync(board, roster, "profile", context, default);
        Assert.Contains("Product spike", gap);
        Assert.Contains("game-quality-assurance", gap);
    }

    internal static WorkOrchestrationPolicyRevision Policy() => JsonSerializer.Deserialize<WorkOrchestrationPolicyRevision>("{}")! with
    {
        RevisionId = Guid.NewGuid(), InitialStageKey = "specialist-execution",
        Stages = new[] { "specialist-execution", "technical-review", "quality", "merge-decision" }
            .Select(key => new WorkOrchestrationStageDefinition(key, key, "AgentExecution", null, "", "{}", "{}", 60, 1, new())).ToArray(),
        Transitions = [new("specialist-execution", "code-published", "technical-review"), new("technical-review", "approved", "quality"), new("quality", "passed", "merge-decision")]
    };
    private static WorkStageExecutionResponse Stage(string key, string status, int attempts) =>
        new(Guid.NewGuid(), key, "AgentExecution", 0, status, "AgentInstallation", null, null, null, attempts, null, null, null, null, DateTimeOffset.UtcNow);
    private static AgentTeammate Member(string role) => new(Guid.NewGuid().ToString(), role, "Agent", role, role, "Peer", "Online")
    { AgentInstallationId = Guid.NewGuid(), RuntimeEligibility = "Eligible", DeclaredRoleKeys = [role], EffectiveCapabilities = ["work.execution.run.v1"] };
}

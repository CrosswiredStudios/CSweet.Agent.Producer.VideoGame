using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ManagerDeliveryTests
{
    [Theory]
    [InlineData("game-technical-director")]
    [InlineData("software-architect")]
    public async Task Approved_brief_survives_restart_populates_once_and_advances_two_sprints(string technicalRole)
    {
        var fixture = new Journey(technicalRole);
        var initial = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Contains("planning", initial.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Items);
        fixture.ReturnPlan = true;
        var started = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Contains("active", started.Message);
        Assert.Equal(4, fixture.Items.Count); // A technical ticket named "epic" must not collide with the grouping Epic.
        Assert.Equal(2, fixture.Sprints.Count);
        Assert.Single(fixture.Started);
        Assert.All(fixture.Items.Values.Where(x => x.Kind == "Task"), x => {
            Assert.Equal(3, x.StageAssignments.Count); Assert.NotNull(x.Delivery);
            Assert.Equal(fixture.Developer, x.AccountableOrganizationUserId);
        });
        await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Single(fixture.Started); Assert.Equal(4, fixture.Items.Count); Assert.Equal(1, fixture.CoordinationStarts);
        fixture.CompleteSprint(1);
        fixture.PreflightValid = false;
        var blocked = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Contains("readiness", blocked.Message); Assert.Single(fixture.Started);
        fixture.PreflightValid = true;
        await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Equal(2, fixture.Started.Count); Assert.Equal(2, fixture.Sprints.Count);
        fixture.CompleteSprint(2);
        for (var stage = 0; stage < 3; stage++) await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Equal("Completed", fixture.Project.Status);
        var final = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.True(final.Complete); Assert.Equal(4, fixture.Items.Count);
    }

    [Fact]
    public async Task No_staffing_approval_does_not_create_a_board_or_start_planning()
    {
        var fixture = new Journey { StaffingApproved = false };
        var result = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Contains("staffing", result.Message); Assert.Equal(0, fixture.SetupCalls); Assert.Empty(fixture.Items);
    }

    [Fact]
    public async Task Active_sprint_reports_current_blockers_and_does_not_start_another_sprint()
    {
        var fixture = new Journey();
        await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        fixture.ReturnPlan = true;
        await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        fixture.ExecutionBlocked = true;
        var result = await new SpecialistAgent().AdvanceManagerDeliveryAsync(fixture.Project.Id, fixture.Context, default);
        Assert.Contains("requires attention", result.Message);
        Assert.Contains("Repository access revoked", result.Message);
        Assert.Single(fixture.Started);
    }
    private sealed class Journey
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Guid Developer { get; } = Guid.NewGuid();
        private readonly Guid _producer = Guid.NewGuid(), _architect = Guid.NewGuid(), _team = Guid.NewGuid(), _board = Guid.NewGuid(), _ready = Guid.NewGuid(), _done = Guid.NewGuid(), _repo = Guid.NewGuid();
        private readonly Guid _installation = Guid.NewGuid(), _developerInstallation = Guid.NewGuid(), _architectInstallation = Guid.NewGuid();
        public WorkstreamDetail Project { get; private set; }
        public AgentRuntimeContext Context { get; }
        public Dictionary<Guid, WorkItem> Items { get; } = [];
        public Dictionary<Guid, WorkSprint> Sprints { get; } = [];
        public List<Guid> Started { get; } = [];
        public bool ReturnPlan { get; set; }
        public bool ExecutionBlocked { get; set; }
        public bool PreflightValid { get; set; } = true;
        public bool StaffingApproved { get; set; } = true;
        public int SetupCalls { get; private set; }
        public int CoordinationStarts { get; private set; }
        private readonly Dictionary<string, WorkItem> _receipts = [];
        private readonly Dictionary<string, WorkSprint> _sprintReceipts = [];
        private readonly Dictionary<string, AgentOperatingStateResponse> _state = [];
        private AgentCoordinationSession? _session;
        private readonly AgentTeamContext _roster;
        private WorkBoardSummary Board => new(_board, "Demo", "Demo", false, false, 1, []) { TeamId = _team, WorkstreamId = Project.Id, ManagerOrganizationUserId = _producer };
        public Journey(string technicalRole = "game-technical-director")
        {
            Project = new(Guid.NewGuid(), "Breakout", "A playable breakout demo", ["Playable"], "concept", "Approved", _producer, null, null, null,
                "video-game-manager-brief.v1", 2, null, "profile-digest", 2);
            AgentTeammate Member(Guid person, Guid installation, string role) => new(person.ToString(), role, "Agent", null, null, "Teammate", "Online")
                { AgentInstallationId = installation, DeclaredRoleKeys = [role], EffectiveCapabilities = role == "software-developer" ? ["work.execution.run.v1", "software-development.implement.v1"] : ["work.execution.run.v1"], RuntimeEligibility = "Eligible" };
            _roster = new(_team.ToString(), "demo", "Demo", 1, _producer.ToString(), "Gabriel",
                [Member(_producer, _installation, "game-producer"), Member(_architect, _architectInstallation, technicalRole), Member(Developer, _developerInstallation, "software-developer")], [], 3, false);
            var runtime = new AgentTestRuntime()
                .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(WorkstreamCapabilityNames.ReadV1, (_, _) => Task.FromResult(Project))
                .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse(StaffingApproved ? [
                    new ResourceChangeRequestResponse(Guid.NewGuid(), Guid.NewGuid(), _producer, _installation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Demo", "Small team", 1, [], [], [], [], null,
                        "Approved", "Fulfilled", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { TeamId = _team }] : [])))
                .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(WorkstreamCapabilityNames.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(_roster, null)))
                .RegisterCapability<PrepareProjectDeliveryRequest, PreparedProjectDelivery>(ProjectDeliveryCapabilities.Prepare, (r, _) => { SetupCalls++; Assert.Equal(3, r.ParticipantIds.Count); return Task.FromResult(new PreparedProjectDelivery(Project.Id, _team, _board, Project.Revision)); })
                .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (r, _) => Task.FromResult(new WorkBoardDetail(Board, [new(_ready, "Ready", "ToDo", 0, "Disabled", null), new(_done, "Done", "Done", 1, "Disabled", null)], Items.Values.ToArray())))
                .RegisterCapability<ConfigureProfileOrchestrationRequest, ConfigureProfileOrchestrationResponse>(WorkOrchestrationCapabilities.ConfigureProfile, (r, _) => Task.FromResult(new ConfigureProfileOrchestrationResponse(Project.Id, _board, 1,
                    new(Guid.NewGuid(), Guid.NewGuid(), _board, 1, "Manager brief", "ready", "ManagerApproval", new(4,4,2,1,1), [], [], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [])))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(_state.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    var state = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint,
                        r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, (_state.GetValueOrDefault(r.StateKey)?.Revision ?? 0)+1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _state[r.StateKey] = state; return Task.FromResult(state);
                })
                .RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, (r, _) => {
                    CoordinationStarts++;
                    _session = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(_producer, _installation, "Gabriel", "game-producer"), new(_architect, _architectInstallation, "Victor", technicalRole),
                        r.Subject, r.Objective, r.SuccessCriteria, "Active", 1, 1, _architect, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])
                        { SourceKind = "Board", WorkContext = new(Guid.NewGuid(), Project.Id, _team, _board, null, null, null, Guid.NewGuid(), null, Project.ProfileKey) };
                    return Task.FromResult(_session);
                })
                .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => {
                    if (!ReturnPlan) return Task.FromResult(_session!);
                    var fingerprint = ProjectDeliveryPlanning.Fingerprint(Project);
                    var plan = new ProjectDeliveryPlan(Project.Id, fingerprint, "Small playable game with isolated logic and integration tests", [
                        new("epic", "Playable loop", "Implement loop", ["Loop"], ["Loop is playable"], [], [], 1, 3),
                        new("effects", "Effects", "Add effects", ["Effects"], ["Effects work"], [], ["epic"], 2, 3)]);
                    return Task.FromResult(_session! with { Status = "Completed", Turns = [new(Guid.NewGuid(), 1, _architect, "Completed", "Plan", DateTimeOffset.UtcNow,
                        new(ProjectDeliveryPlanning.ProposalType, "1.0", fingerprint, 1, true, JsonSerializer.SerializeToElement(plan, Json), "plan-digest"))] });
                })
                .RegisterCapability<CreateWorkItemRequest, WorkItem>(WorkItemCapabilities.Create, (r, _) => {
                    if (_receipts.TryGetValue(r.IdempotencyKey, out var prior)) return Task.FromResult(prior);
                    var item = new WorkItem(Guid.NewGuid(), r.ColumnId ?? _ready, r.ParentItemId, null, r.Kind, r.Title, r.Description ?? "", "Ready", r.Priority, null, 1, 1, null)
                        { Planning = r.Planning, PlanningRevision = 1, ProposalProvenance = r.ProposalProvenance, TypeKey = r.TypeKey, ExecutionMode = r.Kind == "Task" ? "Executable" : "Container" };
                    Items[item.Id] = item; _receipts[r.IdempotencyKey] = item; return Task.FromResult(item);
                })
                .RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (r, _) => {
                    if (_sprintReceipts.TryGetValue(r.IdempotencyKey, out var prior)) return Task.FromResult(prior);
                    var sprint = new WorkSprint(Guid.NewGuid(), _board, r.Name, r.Goal ?? "", "Planned", null, null, null, null, null, 0, 0, 0, 0, 1) { Sequence = r.Sequence };
                    Sprints[sprint.Id] = sprint; _sprintReceipts[r.IdempotencyKey] = sprint; return Task.FromResult(sprint);
                })
                .RegisterCapability<EstimateWorkItemRequest, WorkItem>(WorkItemCapabilities.Estimate, (r, _) => Task.FromResult(Items[r.ItemId] = Items[r.ItemId] with { EstimatePoints = r.EstimatePoints, Revision = r.ExpectedItemRevision + 1 }))
                .RegisterCapability<SetWorkItemSprintRequest, WorkItem>(WorkSprintCapabilities.ManageScope, (r, _) => Task.FromResult(Items[r.ItemId] = Items[r.ItemId] with { SprintId = r.SprintId, Revision = r.ExpectedItemRevision + 1 }))
                .RegisterCapability<FinalizeWorkItemDeliveryRequest, WorkItem>(WorkItemCapabilities.FinalizeDelivery, (r, _) => Task.FromResult(Items[r.ItemId] = Items[r.ItemId] with { Delivery = r.Delivery, StageAssignments = r.StageAssignments, AccountableOrganizationUserId = r.AccountableOrganizationUserId, Revision = r.ExpectedRevision + 1 }))
                .RegisterCapability<ProvisionSourceControlRepositoryRequest, RepositoryProvisioningResult>(SourceControlCapabilities.ProvisionRepository, (_, _) => Task.FromResult(new RepositoryProvisioningResult(Guid.NewGuid(), "Completed", _repo, null, null)))
                .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions, (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([new(_repo, "Game", "InternalGit", "game", "main", "Internal")]))
                .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(Sprints.Values.ToArray()))
                .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Read, (r, _) => Task.FromResult(
                    new WorkSprintExecutionResponse(Guid.NewGuid(), _board, r.SprintId!.Value, Guid.NewGuid(), _producer, "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
                        ExecutionBlocked ? [new(Guid.NewGuid(), Items.Values.First(x => x.Kind == "Task").Id, "DEMO-1", "development", 1, "Blocked", "Repository access revoked", [], DateTimeOffset.UtcNow)] : [])))                .RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintPreflightResult>(WorkOrchestrationCapabilities.Preflight, (r, _) => Task.FromResult(new WorkSprintPreflightResult(PreflightValid, _board, r.SprintId, null,
                    PreflightValid ? [] : [new("test-blocker", "A required assignment changed")])) )
                .RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Start, (r, _) => {
                    Assert.DoesNotContain(Sprints.Values, s => s.Status == "Active"); Started.Add(r.SprintId);
                    Sprints[r.SprintId] = Sprints[r.SprintId] with { Status = "Active", Revision = r.ExpectedSprintRevision + 1 };
                    return Task.FromResult(new WorkSprintExecutionResponse(Guid.NewGuid(), _board, r.SprintId, Guid.NewGuid(), _producer, "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, []));
                })
                .RegisterCapability<MoveWorkItemRequest, WorkItem>(WorkItemCapabilities.Move, (r, _) => Task.FromResult(Items[r.ItemId] = Items[r.ItemId] with { Status = "Completed", Revision = r.ExpectedRevision + 1 }))
                .RegisterCapability<WorkstreamChangeProposalRequest, MutationResponse>(WorkstreamCapabilityNames.ChangeProposeV1, (r, _) => {
                    var phase = r.Changes.GetProperty("lifecycleStage").GetString()!;
                    Project = Project with { LifecycleStage = phase, Revision = Project.Revision + 1, Status = phase == "completed" ? "Completed" : "Active" };
                    return Task.FromResult(new MutationResponse(true, Project.Revision, null, "Applied"));
                });
            // A single capability accepts either board or exact-item reads.
            runtime.RegisterCapability<JsonElement, JsonElement>(WorkItemCapabilities.Read, (r, _) => Task.FromResult(
                r.TryGetProperty("itemId", out var id) && id.ValueKind == JsonValueKind.String ? JsonSerializer.SerializeToElement(Items[id.GetGuid()], Json) :
                JsonSerializer.SerializeToElement(new WorkBoardDetail(Board, [new(_ready, "Ready", "ToDo", 0, "Disabled", null), new(_done, "Done", "Done", 1, "Disabled", null)], Items.Values.ToArray()), Json)));
            Context = runtime.CreateContext(installationId: _installation.ToString(), identity: new(_producer.ToString(), "Gabriel", null, "Producer", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        }
        public void CompleteSprint(int number)
        {
            var sprint = Sprints.Values.Single(s => s.Sequence == number);
            Sprints[sprint.Id] = sprint with { Status = "Completed", Revision = sprint.Revision + 1 };
            foreach (var item in Items.Values.Where(x => x.SprintId == sprint.Id).ToArray()) Items[item.Id] = item with { Status = "Completed", Revision = item.Revision + 1 };
        }
    }
}

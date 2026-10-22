using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DeferredScopeAmendmentTests
{
    [Fact]
    public async Task LongAmendmentSurvivesRestartSourceReauthorizationAndPlanningHandoffWithoutTruncation()
    {
        var direction = string.Concat(Enumerable.Repeat("Detailed owner requirement: preserve responsive behavior, QA and exact source evidence.\n", 180)) + "Final instruction: Chromium only.";
        Assert.True(SpecialistAgent.TryReadAmendmentDirection("Amend ticket VGDEMO-29: " + direction, out var ticket, out var parsed));
        Assert.Equal("VGDEMO-29", ticket); Assert.Equal(direction, parsed);
        var f = new Fixture(direction); f.StopOtherWork();
        await f.CaptureAsync(await f.AgentAsync());
        Assert.Equal(direction, f.Pending.Payload.Deserialize<PendingScopeAmendment>(f.Json)!.Direction);
        var restarted = await f.AgentAsync();
        await restarted.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        Assert.Equal("Queued", f.Pending.Status);
        var handoff = await SpecialistAgent.ReadRoleRepairRequestAsync(SpecialistAgent.ScopeRepairKey(f.Stage.Id, f.Source.TurnId), f.Context, default);
        Assert.Equal(direction, handoff!.ScopeDirection);
        Assert.Equal(1, f.ModelCalls);
    }

    [Fact]
    public async Task Running_work_retains_direction_and_restart_rechecks_boundary_without_resending()
    {
        var f = new Fixture();
        var agent = await f.AgentAsync();
        Assert.Contains("I saved", await f.CaptureAsync(agent));
        Assert.Contains("I saved", await f.CaptureAsync(agent)); // Duplicate event delivery.
        Assert.Equal("Pending", f.Pending.Status);
        Assert.Single(f.Todos);
        Assert.Equal(0, f.ModelCalls);
        var todo = f.Todos[0];
        var wait = await agent.HandlePersonalTodoAsync(todo, f.Context, default);
        Assert.Contains("saved", ResultContent(wait));
        Assert.Equal(0, f.ModelCalls);
        Assert.Equal("Running", f.Execution.Items[1].Status);

        // A persisted wait has a future deadline; a safe-boundary wake must bypass it.
        f.Todos[0] = todo with { Status = "Running", Wait = new(DateTimeOffset.UtcNow.AddHours(1), "Waiting") };
        f.StopOtherWork();
        agent = await f.AgentAsync();
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Equal(1, f.Requeues);
        var result = await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        Assert.Contains("queued for corrected planning", ResultContent(result));
        Assert.Equal("Queued", f.Pending.Status);
        Assert.Equal(1, f.ModelCalls);
        Assert.Equal(2, f.Todos.Count); // Deferred request and the exact planning successor.
        var role = await SpecialistAgent.ReadRoleRepairRequestAsync(SpecialistAgent.ScopeRepairKey(f.Stage.Id, f.Source.TurnId), f.Context, default);
        Assert.Equal(f.Source.TurnId, role!.ScopeAuthorizingTurnId);
        Assert.Equal(f.Direction, role.ScopeDirection);
        await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Equal(1, f.ModelCalls);
        Assert.Equal(2, f.Todos.Count);
        Assert.All(f.Execution.Items, x => Assert.NotEqual("Cancelled", x.Status));
    }

    [Fact]
    public async Task Lost_agenda_creation_is_repaired_from_the_saved_board_request()
    {
        var f = new Fixture { FailNextAgenda = true };
        var agent = await f.AgentAsync();
        await Assert.ThrowsAsync<IOException>(() => f.CaptureAsync(agent));
        Assert.Equal("Pending", f.Pending.Status);
        Assert.Empty(f.Todos);
        agent = await f.AgentAsync();
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Single(f.Todos);
        Assert.Equal(0, f.ModelCalls);
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Single(f.Todos);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_handoff_reuses_exact_planning_and_does_not_repeat_model_work(bool savedBeforeInterruption)
    {
        var f = new Fixture();
        f.StopOtherWork();
        var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        f.FailQueuedWrite = true;
        f.SaveQueuedBeforeFailure = savedBeforeInterruption;
        await Assert.ThrowsAsync<IOException>(() => agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default));
        Assert.Equal(savedBeforeInterruption ? "Queued" : "Pending", f.Pending.Status);
        Assert.Equal(1, f.ModelCalls);
        agent = await f.AgentAsync();
        await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        Assert.Equal("Queued", f.Pending.Status);
        Assert.Equal(1, f.ModelCalls);
        Assert.Equal(2, f.Todos.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_capture_rereads_the_winner_without_overwriting_its_direction(bool conflicting)
    {
        var f = new Fixture { CaptureConflict = conflicting ? "different" : "same" };
        var agent = await f.AgentAsync();
        var result = await f.CaptureAsync(agent);
        Assert.Contains(conflicting ? "retained first" : "I saved", result);
        Assert.Equal(conflicting ? 0 : 1, f.Todos.Count);
        Assert.Equal(conflicting ? "A different authenticated direction" : f.Direction,
            f.Pending.Payload.Deserialize<PendingScopeAmendment>(f.Json)!.Direction);
        Assert.Equal(0, f.ModelCalls);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("ownership")]
    [InlineData("requester")]
    [InlineData("deleted-source")]
    [InlineData("edited-source")]
    [InlineData("denied-source-read")]
    [InlineData("cancelled")]
    [InlineData("new-stage")]
    [InlineData("completed-anchor")]
    public async Task Changed_scope_or_authority_is_held_without_model_or_sprint_mutation(string change)
    {
        var f = new Fixture(); var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        f.StopOtherWork();
        switch (change)
        {
            case "planning": f.Detail = f.Detail with { Items = [f.Item with { PlanningRevision = 2 }] }; break;
            case "ownership": f.Detail = f.Detail with { Board = f.Detail.Board with { ManagerOrganizationUserId = Guid.NewGuid() } }; break;
            case "requester": f.Authorized = false; break;
            case "deleted-source": f.Messages.Clear(); break;
            case "edited-source": f.Messages[0] = f.Messages[0] with { Content = "Amend ticket VGDEMO-29: A different direction." }; break;
            case "denied-source-read": f.DenySourceRead = true; break;
            case "cancelled": f.Execution = f.Execution with { Status = "Cancelled" }; break;
            case "new-stage": f.Execution = f.Execution with { Items = [f.Execution.Items[0] with { Stages = [f.Stage with { Id = Guid.NewGuid() }] }] }; break;
            case "completed-anchor": f.Execution = f.Execution with { Items = [f.Execution.Items[0] with { Status = "Completed" }] }; break;
        }
        var result = await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        Assert.Contains("needs review", ResultContent(result));
        Assert.Equal("NeedsReview", f.Pending.Status);
        Assert.Equal(0, f.ModelCalls);
        Assert.Single(f.Todos);
    }

    [Fact]
    public async Task A_second_direction_cannot_overwrite_a_waiting_authorized_request()
    {
        var f = new Fixture(); var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        var result = await agent.RetainScopeAmendmentAsync(f.Execution, f.Execution.Items[0], f.Stage, "Conflicting scope",
            f.Source with { TurnId = Guid.NewGuid() }, f.Context, default);
        Assert.Contains("already retained", result);
        Assert.Equal(f.Direction, f.Pending.Payload.Deserialize<PendingScopeAmendment>(f.Json)!.Direction);
        Assert.Single(f.Todos);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("spoofed-reply", false)]
    [InlineData("unauthorized-sender", false)]
    [InlineData("quoted-command", false)]
    [InlineData("new-stage", false)]
    [InlineData("different-turn", false)]
    [InlineData("later-direction", false)]
    public async Task Upgrade_recovers_only_the_exact_previously_refused_authorized_turn(string scenario, bool recover)
    {
        var f = new Fixture();
        f.Messages.Add(new(Guid.NewGuid(), 2, f.Source.ConversationId,
            scenario == "spoofed-reply" ? f.Requester : f.Producer, "Producer", "Agent", SpecialistAgent.LegacyScopeRefusal,
            f.Now, f.Source.TurnId));
        if (scenario == "unauthorized-sender") f.Authorized = false;
        if (scenario == "quoted-command") f.Messages[0] = f.Messages[0] with { Content = "Quote: " + f.Messages[0].Content };
        if (scenario == "new-stage") f.Stage = f.Stage with { UpdatedAt = f.Now.AddMinutes(1) };
        if (scenario == "different-turn") f.Messages[0] = f.Messages[0] with { ChatTurnId = Guid.NewGuid() };
        if (scenario == "later-direction") f.Messages.Add(f.Messages[0] with { Id = Guid.NewGuid(), Sequence = 3, Content = "Amend ticket VGDEMO-29: Keep the measurement requirement." });
        f.Execution = f.Execution with { Items = [f.Execution.Items[0] with { Stages = [f.Stage] }, f.Execution.Items[1]] };
        var agent = await f.AgentAsync();
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Equal(recover ? 1 : 0, f.Todos.Count);
        Assert.Equal(recover, f.States.ContainsKey(SpecialistAgent.PendingScopeKey(f.Board)));
        Assert.Equal(0, f.ModelCalls);
        await agent.RecoverPendingScopeAmendmentsAsync(f.Context, default);
        Assert.Equal(recover ? 1 : 0, f.Todos.Count);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("revoked-authority")]
    [InlineData("changed-original")]
    public async Task FreshSameDirectionRenewsBlockedCorrectionAgainstCurrentSourceWithoutRepeatingInference(string scenario)
    {
        var f = new Fixture(); f.StopOtherWork();
        var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        var priorKey = SpecialistAgent.ScopeRepairKey(f.Stage.Id, f.Source.TurnId);
        f.Todos[1] = f.Todos[1] with { Status = "Blocked" };
        f.Item = f.Item with { PlanningRevision = 2, Planning = f.Item.Planning! with { Constraints = ["Current approved constraint"] } };
        if (scenario == "changed-original") f.Item = f.Item with { Planning = f.Item.Planning! with { AcceptanceCriteria = ["Different criterion"] } };
        f.Detail = f.Detail with { Items = [f.Item] };
        f.Source = f.Source with { MessageId = Guid.NewGuid(), TurnId = Guid.NewGuid() };
        f.Messages.Add(new(f.Source.MessageId, 2, f.Source.ConversationId, f.Requester, "Owner", "Human",
            "Amend ticket VGDEMO-29: " + f.Direction, f.Now, f.Source.TurnId));
        Assert.Contains("I saved", await f.CaptureAsync(agent));
        Assert.Equal(priorKey, f.Pending.Payload.Deserialize<PendingScopeAmendment>(f.Json)!.PriorRepairStateKey);
        if (scenario == "revoked-authority") f.Authorized = false;
        await agent.HandlePersonalTodoAsync(f.Todos[2], f.Context, default);
        Assert.Equal(1, f.ModelCalls);
        Assert.Equal(scenario == "valid" ? "Queued" : "NeedsReview", f.Pending.Status);
        var renewed = await SpecialistAgent.ReadRoleRepairRequestAsync(SpecialistAgent.ScopeRepairKey(f.Stage.Id, f.Source.TurnId), f.Context, default);
        if (scenario == "valid")
        {
            Assert.NotNull(renewed);
            Assert.Equal(new[] { "Current approved constraint" }, renewed.OriginalItems[0].Planning!.Constraints);
            Assert.Equal(f.Source.TurnId, renewed.ScopeAuthorizingTurnId);
            Assert.Equal(priorKey, SpecialistAgent.RoleRepairStateKey((await SpecialistAgent.ReadRoleRepairRequestAsync(priorKey, f.Context, default))!));
        }
        else Assert.Null(renewed);
    }

    [Fact]
    public async Task FreshDirectionCannotReplaceAnActiveCorrection()
    {
        var f = new Fixture(); f.StopOtherWork();
        var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        f.Todos[1] = f.Todos[1] with { Status = "Running" };
        var priorTurn = f.Source.TurnId;
        f.Source = f.Source with { MessageId = Guid.NewGuid(), TurnId = Guid.NewGuid() };
        Assert.Contains("already retained", await f.CaptureAsync(agent));
        Assert.Equal(priorTurn, f.Pending.Payload.Deserialize<PendingScopeAmendment>(f.Json)!.Source.TurnId);
        Assert.Equal(2, f.Todos.Count);
    }

    [Fact]
    public async Task OrdinaryPlanningWaitsForScopeOwnerAndAppliedStateIsSourceBoundAndIdempotent()
    {
        var f = new Fixture(); f.StopOtherWork();
        var agent = await f.AgentAsync();
        await f.CaptureAsync(agent);
        var ordinary = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { CorrelationId = "producer-planning:test",
            WorkContext = new(WorkstreamId: f.Detail.Board.WorkstreamId, TeamId: f.Detail.Board.TeamId, BoardId: f.Board, SourceFingerprint: "test") };
        Assert.Contains("owns the planning boundary", ResultContent(await agent.HandlePersonalTodoAsync(ordinary, f.Context, default)));
        await agent.HandlePersonalTodoAsync(f.Todos[0], f.Context, default);
        Assert.Contains("owns the planning boundary", ResultContent(await agent.HandlePersonalTodoAsync(ordinary, f.Context, default)));
        var request = (await SpecialistAgent.ReadRoleRepairRequestAsync(SpecialistAgent.ScopeRepairKey(f.Stage.Id, f.Source.TurnId), f.Context, default))!;
        await SpecialistAgent.MarkScopeAppliedAsync(request with { ScopeAuthorizingTurnId = Guid.NewGuid() }, f.Context, default);
        Assert.Equal("Queued", f.Pending.Status);
        await SpecialistAgent.MarkScopeAppliedAsync(request, f.Context, default);
        Assert.Equal("Applied", f.Pending.Status);
        var revision = f.Pending.Revision;
        await SpecialistAgent.MarkScopeAppliedAsync(request, f.Context, default);
        Assert.Equal(revision, f.Pending.Revision);
    }

    private static string ResultContent(PersonalTodoResult result) => (string)typeof(PersonalTodoResult)
        .GetProperty("Content", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(result)!;

    private sealed class Fixture
    {
        public readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public readonly Guid Producer = Guid.NewGuid(), Requester = Guid.NewGuid(), Board = Guid.NewGuid(), Sprint = Guid.NewGuid();
        public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly string Direction = "Defer sprite measurement; record owner risk acceptance, never a passed benchmark.";
        public readonly Dictionary<string, AgentOperatingStateResponse> States = new();
        public readonly List<PersonalTodoItem> Todos = new();
        public readonly List<CommunicationMessage> Messages = new();
        public ScopeAmendmentSource Source;
        public WorkStageExecutionResponse Stage;
        public WorkItem Item;
        public WorkBoardDetail Detail;
        public WorkSprintExecutionResponse Execution;
        public AgentRuntimeContext Context;
        public bool Authorized = true, FailNextAgenda, FailQueuedWrite, SaveQueuedBeforeFailure, DenySourceRead;
        public string? CaptureConflict;
        public int ModelCalls, Requeues;
        public AgentOperatingStateResponse Pending => States[SpecialistAgent.PendingScopeKey(Board)];

        public Fixture(string? direction = null)
        {
            if (direction is not null) Direction = direction;
            Source = new(Requester, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Stage = new(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0, "Blocked", "AgentInstallation", Producer,
                Guid.NewGuid(), null, 1, "blocked", null, null, null, Now.AddMinutes(-5));
            Item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = Guid.NewGuid(), SprintId = Sprint, Identifier = "VGDEMO-29", Status = "Blocked",
                PlanningRevision = 1, Planning = new(["Record real measurements"], ["60 second benchmark"], []) };
            Detail = new(new(Board, "Game", "", false, false, 1, []) { ManagerOrganizationUserId = Producer, WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid() }, [], [Item]);
            var anchor = new WorkItemExecutionResponse(Guid.NewGuid(), Item.Id, "VGDEMO-29", Stage.StageKey, 0, "Blocked", null, [Stage], Now);
            Execution = new(Guid.NewGuid(), Board, Sprint, Guid.NewGuid(), Producer, "Active", 1, Now.AddMinutes(-10), Now, null,
                [anchor, anchor with { Id = Guid.NewGuid(), WorkItemId = Guid.NewGuid(), ItemIdentifier = "VGDEMO-31", Status = "Running", Stages = [Stage with { Id = Guid.NewGuid(), Status = "Running" }] }]);
            Messages.Add(new(Source.MessageId, 1, Source.ConversationId, Requester, "Owner", "Human", "Amend ticket VGDEMO-29: " + Direction, Now, Source.TurnId));
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) =>
                {
                    var interruptQueued = FailQueuedWrite && r.Status == "Queued";
                    if (interruptQueued && !SaveQueuedBeforeFailure) { FailQueuedWrite = false; throw new IOException("Lost handoff response"); }
                    var old = States.GetValueOrDefault(r.StateKey);
                    if (old is not null && old.Revision != r.ExpectedRevision) throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite, PlatformCapabilityErrorCode.Conflict, "Stale write");
                    var state = new AgentOperatingStateResponse(old?.Id ?? Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions,
                        r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, (old?.Revision ?? 0) + 1, Now, Now);
                    if (CaptureConflict is { } conflict && r.Status == "Pending")
                    {
                        CaptureConflict = null;
                        var payload = state.Payload.Deserialize<PendingScopeAmendment>(Json)!;
                        if (conflict == "different") state = state with { Payload = JsonSerializer.SerializeToElement(payload with {
                            Direction = "A different authenticated direction", Source = payload.Source with { TurnId = Guid.NewGuid() } }, Json) };
                        States[r.StateKey] = state;
                        throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite, PlatformCapabilityErrorCode.Conflict, "Concurrent capture");
                    }
                    States[r.StateKey] = state;
                    if (interruptQueued) { FailQueuedWrite = false; throw new IOException("Lost successful handoff response"); }
                    return Task.FromResult(state);
                })
                .RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([Detail.Board]))
                .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(Detail))
                .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse>(WorkManagementCapabilityNames.OrchestrationRead, (_, _) => Task.FromResult(Execution))
                .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(
                    [new(Sprint, Board, "Sprint", "", "Active", null, null, Now, null, 10, 1, 0, 1, 0, 1)]))
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new(Guid.NewGuid(), Producer, "Producer", Requester, "Owner", 1, Todos)], Producer)))
                .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) =>
                {
                    if (FailNextAgenda) { FailNextAgenda = false; throw new IOException("Interrupted before agenda persistence"); }
                    var item = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Status = "Ready", CorrelationId = r.CorrelationId, WorkContext = r.WorkContext };
                    Todos.Add(item); return Task.FromResult(item);
                })
                .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) =>
                {
                    Requeues++; var index = Todos.FindIndex(x => x.Id == r.ItemId);
                    Todos[index] = Todos[index] with { Status = "Ready", Wait = null }; return Task.FromResult(Todos[index]);
                })
                .RegisterCapability<JsonElement, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead, (_, _) => Task.FromResult(
                    new OrganizationSnapshotResponse(Guid.NewGuid(), "Active", [new(Producer, "Producer", "Agent", null, Authorized ? Requester : null, null, true),
                        new(Requester, "Owner", "Human", null, null, null, true)], [], [], [], [], Now)))
                .RegisterCapability<JsonElement, JsonElement>(CommunicationCapabilities.ChatRead, (r, _) =>
                {
                    if (DenySourceRead) throw new PlatformCapabilityException(CommunicationCapabilities.ChatRead, PlatformCapabilityErrorCode.Denied, "Current source access revoked");
                    return Task.FromResult(r.TryGetProperty("chatId", out var chatId) ? JsonSerializer.SerializeToElement(new CommunicationMessages(Messages), Json) : JsonSerializer.SerializeToElement(
                        new CommunicationHub(Producer, Producer, false, true, [new(Source.ConversationId, "Owner", null, true, true, false, false, Now, [], null, null, 0)]), Json));
                })
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) =>
                {
                    ModelCalls++;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(new ScopeAmendmentPlan(
                        [new(Item.Id, "acceptanceCriteria", "60 second benchmark", "Owner accepted unmeasured risk; benchmark deferred")])), role = "assistant" }));
                });
            Context = runtime.CreateContext(identity: new(Producer.ToString(), "Producer", null, "Producer", null, [], null, Requester.ToString(), "Owner"));
        }

        public void StopOtherWork() => Execution = Execution with { Items = [Execution.Items[0], Execution.Items[1] with { Status = "Completed", Stages = [Execution.Items[1].Stages[0] with { Status = "Completed" }] }] };
        public async Task<SpecialistAgent> AgentAsync()
        {
            var agent = new SpecialistAgent();
            await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update, JsonSerializer.SerializeToElement(
                new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), Context, default);
            return agent;
        }
        public Task<string> CaptureAsync(SpecialistAgent agent) => agent.RetainScopeAmendmentAsync(Execution, Execution.Items[0], Stage, Direction, Source, Context, default);
    }
}

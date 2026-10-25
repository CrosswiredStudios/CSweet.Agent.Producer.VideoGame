using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    // One retained board, proposal, planning commitment and sprint history across
    // acceptance, recovery, refinement, estimates, QA readiness and real agent start callbacks.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_increment_recovers_planning_and_starts_successor_once_across_restart_and_duplicate_wakes(bool pendingAuthority)
    {
        var f = new Fixture();
        var initial = await f.Advance(null); f.CompleteRefinement();
        var refined = await f.Advance(initial.Progress);
        await Publish(f.Roster);
        var progress = refined.Progress with { ScheduledArtifactDigest = f.DetailArtifact!.Digest };
        for (var i = 0; i < f.Items.Count; i++)
            if (f.Items[i].ExecutionMode == WorkItemExecutionModes.Executable) f.Items[i] = f.Items[i] with { Status = "Completed" };
        var completedIds = f.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Executable).Select(x => x.Id).ToArray();
        var epic = f.Items.Single(x => x.Kind == "Epic" && x.ProposalProvenance!.ProposalItemKey == "prototype");
        var followup = epic with { Id = Guid.NewGuid(), Title = "[release-qa-follow-up] Deferred", ProposalProvenance = null };
        f.Items.AddRange([followup, followup with { Id = Guid.NewGuid(), Kind = "Story" }, followup with { Id = Guid.NewGuid(), Kind = "Task" }]);
        var plan = JsonSerializer.Deserialize<WorkDeliveryPlanResponse>("{}")! with { Id = Guid.NewGuid(), Status = "Active", EpicItemIds = [epic.Id] };
        f.Plans.Add(plan);
        Assert.NotNull((await f.Advance(progress)).Waiting); Assert.Single(f.Starts);

        var states = new Dictionary<string, AgentOperatingStateResponse>();
        ProducerOperatingState Portfolio() => new() { PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState> {
            [f.Cycle.WorkstreamId] = new(f.Cycle.WorkstreamId, f.Cycle.BoardId, f.Cycle.TeamId, f.Cycle.PlanningFingerprint,
                null, null, null, null, f.DetailArtifact!.Digest, "reconciled", DateTimeOffset.UtcNow) {
                Progressive = progress,
                OutstandingAuthorityQuestions = pendingAuthority ? ["Specific Phaser version selection for particle performance"] : [],
                EngineeringPrerequisiteKeys = pendingAuthority ? ["next-implementation"] : []
            } } };
        f.Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey.Contains("portfolio") ? State(r.StateKey, JsonSerializer.SerializeToElement(Portfolio())) : states.GetValueOrDefault(r.StateKey))));
        f.Runtime.RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
            (r, _) => Task.FromResult(states[r.StateKey] = State(r.StateKey, r.Payload)));
        var root = JsonSerializer.Deserialize<PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Revision = 10, Status = "Blocked",
            CorrelationId = "producer-planning:retained", BlockReason = "Repeated issue: I couldn’t finish this attempt because execution stopped unexpectedly." };
        var todos = new List<PersonalTodoItem> { root }; var requeues = 0;
        f.Runtime.RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(
            new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Producer", null, null, 1, todos)])));
        f.Runtime.RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) => {
            var index = todos.FindIndex(x => x.Id == r.ItemId); Assert.Equal(todos[index].Revision, r.ExpectedRevision);
            requeues++; return Task.FromResult(todos[index] = todos[index] with { Status = "Ready", Wait = null, Revision = todos[index].Revision + 1 }); });
        async Task Discover()
        {
            var continuation = await SpecialistAgent.ReadProgressiveContinuationAsync(f.Cycle.WorkstreamId, f.Cycle.BoardId,
                f.Cycle.PlanningFingerprint, f.Runtime.CreateContext(), default);
            await SpecialistAgent.EnsureCommitmentAsync(root.CorrelationId!, "Plan", "Continue", "High", new(),
                f.Runtime.CreateContext(), default, progressiveContinuation: continuation);
        }
        await Discover(); Assert.Equal(0, requeues); // Tasks complete does not mean accepted.
        f.Plans[0] = plan with { Status = "Completed" };
        await Discover(); await Discover(); Assert.Equal(1, requeues);
        // Another crash cannot reset its budget just because the same acceptance event replays.
        todos[0] = todos[0] with { Status = "Blocked", Revision = 12 };
        await Discover(); Assert.Equal(1, requeues);
        var next = await f.Advance(progress); Assert.Equal("remaining", next.Progress.PendingStoryKey); Assert.Equal(2, f.Starts.Count);
        await f.Advance(next.Progress); Assert.Equal(2, f.Starts.Count);

        var nextStory = f.Outline.DeliveryItems.Single(x => x.ProposalKey == "remaining");
        var task = nextStory with { ProposalKey = "next-implementation", WorkItemTypeKey = VideoGameWorkItemTypeKeys.Task,
            ParentProposalKey = "remaining", AccountableRoleKey = "game-engineer" };
        f.Detail = f.Detail! with { DeliveryItems = [.. f.Detail!.DeliveryItems, task] };
        var evidence = f.Evidence with { Phase = "Story", DetailedStoryKeys = ["playable", "remaining"] };
        var payload = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(f.Detail))!.AsObject();
        payload["progressivePlanning"] = JsonSerializer.SerializeToNode(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        f.DetailArtifact = new("video-game.production.technical-delivery-proposal.v1", "1.0", "plan", 1, true, JsonSerializer.SerializeToElement(payload), "next-details");
        f.Refinement = f.Refinement! with { Status = "Completed", Turns = [new(Guid.NewGuid(), 1, f.Person.OrganizationUserId,
            "complete", "Complete", DateTimeOffset.UtcNow, f.DetailArtifact)] };
        var nextRefined = await f.Advance(next.Progress); Assert.NotNull(nextRefined.Session);
        AgentTeammate Member(string role) => new(Guid.NewGuid().ToString(), role, "Agent", null, null, "Peer", "Online") {
            AgentInstallationId = Guid.NewGuid(), IsAvailable = true, RuntimeEligibility = "Eligible", DeclaredRoleKeys = [role], EffectiveCapabilities = ["work.execution.run"] };
        var engineer = Member("game-engineer"); var qa = Member("game-quality-assurance");
        var roster = f.Roster with { Members = [engineer, qa] };
        await Publish(roster); progress = nextRefined.Progress with { ScheduledArtifactDigest = f.DetailArtifact.Digest };
        var now = DateTimeOffset.UtcNow; var ready = Guid.NewGuid(); var repository = Guid.NewGuid();
        var board = new WorkBoardSummary(f.Cycle.BoardId, "GAME", "Game", false, false, 1, []) { TeamId = f.Cycle.TeamId, WorkstreamId = f.Cycle.WorkstreamId };
        var sprint1 = new WorkSprint(Guid.NewGuid(), board.Id, "Production Sprint 1", "First increment", "Completed", null, null, null, now, null, 0, 0, 0, 0, 1) { Sequence = 1 };
        var sprints = new List<WorkSprint> { sprint1 }; var starts = 0; var preflightValid = false; var preflights = 0;
        WorkItem Update(Guid id, Func<WorkItem, WorkItem> change) { var index = f.Items.FindIndex(x => x.Id == id); return f.Items[index] = change(f.Items[index]); }
        f.Runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(new WorkBoardDetail(board,
            [new(ready, "Ready", "ToDo", 0, "None", null)], f.Items)));
        f.Runtime.RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(sprints));
        f.Runtime.RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (r, _) => {
            Assert.Equal(2, r.Sequence); var created = sprint1 with { Id = Guid.NewGuid(), Name = r.Name, Status = "Planned", Sequence = 2 };
            sprints.Add(created); return Task.FromResult(created); });
        f.Runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(roster, f.Cycle.WorkstreamId)));
        f.Runtime.RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) => Task.FromResult(JsonSerializer.Deserialize<WorkstreamDetail>("{}")! with {
            Id = f.Cycle.WorkstreamId, ProfileDefinitionDigest = f.Cycle.ProfileDigest }));
        var sessions = new Dictionary<string, AgentCoordinationSession>();
        f.Runtime.RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, (r, _) => {
            if (sessions.TryGetValue(r.IdempotencyKey, out var existing)) return Task.FromResult(existing);
            object result; string type;
            if (r.Artifact!.Type == "video-game.production.role-estimate-request.v1") {
                var request = r.Artifact.Payload.Deserialize<GameRoleEstimateCapacityRequestV1>()!;
                result = new GameRoleEstimateCapacityProposalV1(request.BoardId, request.RoleKey, request.PlanningRevision, request.PlanningDigest,
                    request.WorkItems.Select(x => new GameWorkItemEstimateV1(x.WorkItemId, 3, "high")).ToArray(), 8, [], [], "estimates");
                type = "video-game.production.role-estimate-capacity-proposal.v1";
            } else {
                var request = r.Artifact.Payload.Deserialize<GameQaSprintReadinessRequestV1>()!;
                result = new GameQaSprintReadinessAssessmentV1(Guid.Empty, request.PlanningRevision, request.PlanningDigest, true,
                    request.Candidates.Select(x => x.WorkItemId).ToArray(), [], "readiness"); type = "video-game.production.qa-sprint-readiness-assessment.v1";
            }
            var author = roster.Members.Single(x => x.EmployeeId == r.TargetOrganizationUserId.ToString());
            var person = new AgentCoordinationParticipant(Guid.Parse(author.EmployeeId), author.AgentInstallationId!.Value, author.DisplayName, "Specialist");
            return Task.FromResult(sessions[r.IdempotencyKey] = f.Session with { Id = Guid.NewGuid(), Target = person, Status = "Completed",
                Turns = [new(Guid.NewGuid(), 1, person.OrganizationUserId, "Complete", "Evidence", now,
                    new(type, "1.0", "evidence", 1, true, JsonSerializer.SerializeToElement(result), r.IdempotencyKey))] }); });
        f.Runtime.RegisterCapability<EstimateWorkItemRequest, WorkItem>(WorkItemCapabilities.Estimate, (r, _) => Task.FromResult(Update(r.ItemId,
            x => x with { EstimatePoints = r.EstimatePoints, EstimateProvenance = r.Provenance, Revision = x.Revision + 1 })));
        f.Runtime.RegisterCapability<MoveWorkItemRequest, WorkItem>(WorkItemCapabilities.Move, (r, _) => Task.FromResult(Update(r.ItemId, x => x with { ColumnId = ready, Status = "Ready" })));
        f.Runtime.RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkItemCapabilities.Comment, (r, _) => Task.FromResult(new WorkItemComment(Guid.NewGuid(), r.ItemId, "Agent", Guid.NewGuid(), "QA", r.Body, 1, now, null)));
        f.Runtime.RegisterCapability<SetWorkItemSprintRequest, WorkItem>(WorkSprintCapabilities.ManageScope, (r, _) => Task.FromResult(Update(r.ItemId, x => x with { SprintId = r.SprintId })));
        f.Runtime.RegisterCapability<SetWorkSprintCapacityRequest, WorkSprint>(WorkSprintCapabilities.ManageCapacity, (r, _) => { sprints[1] = sprints[1] with { CapacityPoints = r.CapacityPoints }; return Task.FromResult(sprints[1]); });
        f.Runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => {
            var item = root with { Id = Guid.NewGuid(), CorrelationId = r.CorrelationId, Status = "Ready", WorkContext = r.WorkContext }; todos.Add(item); return Task.FromResult(item); });
        f.Runtime.RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions, (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([new(repository, "Game", "InternalGit", "game", "main", "PullRequest")]));
        f.Runtime.RegisterCapability<FinalizeWorkItemDeliveryRequest, WorkItem>(WorkItemCapabilities.FinalizeDelivery, (r, _) => Task.FromResult(Update(r.ItemId, x => x with { Delivery = r.Delivery })));
        f.Runtime.RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintPreflightResult>(WorkOrchestrationCapabilities.Preflight, (_, _) => {
            preflights++; Assert.All(f.Items.Where(x => x.SprintId == sprints[1].Id), x => Assert.NotNull(x.Delivery));
            return Task.FromResult(new WorkSprintPreflightResult(preflightValid, board.Id, sprints[1].Id, null, preflightValid ? [] : [new("gate.pending", "Readiness changed.")])); });
        f.Runtime.RegisterCapability<StartWorkSprintExecutionRequest, WorkSprintExecutionResponse>(WorkOrchestrationCapabilities.Start, (_, _) => {
            starts++; sprints[1] = sprints[1] with { Status = "Active" }; return Task.FromResult(JsonSerializer.Deserialize<WorkSprintExecutionResponse>("{}")! with { Status = "Active" }); });
        await SpecialistAgent.EnsureDraftSprintAsync(board.Id, "reconciled", f.Runtime.CreateContext(), default);
        var estimate = root with { CorrelationId = SpecialistAgent.SprintEstimationCorrelation(board.Id, "reconciled", 2),
            WorkContext = new(f.Cycle.WorkstreamId, f.Cycle.TeamId, board.Id, SourceFingerprint: "reconciled") };
        await new SpecialistAgent().HandlePersonalTodoAsync(estimate, f.Runtime.CreateContext(), default);
        var readiness = Assert.Single(todos.Where(x => x.CorrelationId!.StartsWith("producer-readiness:")));
        Assert.Single(f.Items.Where(x => x.SprintId == sprints[1].Id)); Assert.Equal(0, starts);
        await new SpecialistAgent().HandlePersonalTodoAsync(readiness, f.Runtime.CreateContext(), default); Assert.Equal(0, starts);
        preflightValid = true;
        await new SpecialistAgent().HandlePersonalTodoAsync(readiness, f.Runtime.CreateContext(), default);
        await new SpecialistAgent().HandlePersonalTodoAsync(readiness, f.Runtime.CreateContext(), default);
        Assert.Equal(1, starts); Assert.Equal(2, preflights); Assert.Equal(2, sprints.Count);
        Assert.All(f.Items.Where(x => completedIds.Contains(x.Id)), x => Assert.Equal("Completed", x.Status));
        Assert.Equal(3, f.Items.Count(x => x.ProposalProvenance is null));

        Task Publish(AgentTeamContext roster) => SpecialistAgent.PublishCanonicalBacklogAsync(f.Cycle.BoardId, roster, f.Cycle, [], f.Refinement!, f.DetailArtifact!,
            new(f.Cycle, [], [], [], "accepted"), f.Refinement!, f.DetailArtifact!, f.Detail!, f.Runtime.CreateContext(), default);
        static AgentOperatingStateResponse State(string key, JsonElement payload) => new(Guid.NewGuid(), key, "test", 1, "Active", new Dictionary<string, string>(), [], "", [], Guid.Empty, payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}

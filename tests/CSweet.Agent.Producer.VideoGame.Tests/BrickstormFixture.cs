using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

// Reconstructed from the November 17 Brickstorm review. The rejection is the retained diagnostic.
// IDs, times, portfolio, roster, sprint, package and card are synthetic minimal planning inputs.
internal sealed class BrickstormFixture
{
    public const string SpecialistFailure = "Page failed after three attempts: approved outcomes and constraints (finish=stop, characters=18092): 2 commitment(s) do not cite a valid accepted source index.";
    public string BusinessId { get; } = Guid.NewGuid().ToString();
    public AgentIdentity Identity { get; }
    public AgentTestRuntime Runtime { get; }
    public PersonalTodoItem Card { get; }
    public List<WorkSprint> Sprints { get; } = [];
    public List<StartBoardCoordinationRequest> Requests { get; } = [];
    public Dictionary<string, AgentCoordinationSession> AcceptedSessions { get; } = [];

    public BrickstormFixture(bool loseFirstRetryResponse = false)
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var producer = Guid.NewGuid();
        var director = Guid.NewGuid();
        var victor = Guid.NewGuid();
        var victorInstallation = Guid.NewGuid();
        var project = Guid.NewGuid();
        var team = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string fingerprint = "planning-fingerprint";
        var workstream = JsonSerializer.Deserialize<WorkstreamDetail>(JsonSerializer.Serialize(new { id = project, name = "Brickstorm", outcome = "Ship it",
            successCriteria = Array.Empty<string>(), lifecycleStage = "concept", status = "Approved",
            accountableManagerOrganizationUserId = producer, profileDefinitionDigest = "digest" }, web), web)!;
        var roster = new AgentTeamContext(team.ToString(), "video-game-team", "Video Game Team", 1, director.ToString(), "Marcus", [
            new(victor.ToString(), "Victor", "Agent", null, null, "Peer", "Online") { AgentInstallationId = victorInstallation, IsAvailable = true,
                RuntimeEligibility = "Eligible", DeclaredRoleKeys = ["game-technical-director"], EffectiveCapabilities = ["work.execution.run"] }], [], 1, false);
        var handoff = new ProducerAcceptedHandoff(project, Guid.NewGuid(), Guid.NewGuid(), "brief-digest", "handoff-digest", Guid.NewGuid(), packageId, 1, DateTimeOffset.UtcNow);
        var portfolio = new ProducerOperatingState {
            AcceptedHandoffs = new Dictionary<Guid, ProducerAcceptedHandoff> { [project] = handoff },
            PlanningCycles = new Dictionary<Guid, ProducerPlanningCycleState> {
                [project] = new(project, boardId, team, fingerprint, null, null, null, null, null, null, DateTimeOffset.UtcNow) } };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        AgentOperatingStateResponse Save(AgentOperatingStateWriteRequest request)
        {
            var previous = states.GetValueOrDefault(request.StateKey);
            if (request.ExpectedRevision is long expected && expected != (previous?.Revision ?? 0))
                throw new InvalidOperationException($"Stale fixture write for {request.StateKey} at revision {expected}.");
            var now = DateTimeOffset.UtcNow;
            return states[request.StateKey] = new(previous?.Id ?? Guid.NewGuid(), request.StateKey, request.SchemaId,
                request.SchemaVersion, request.Status, request.SourceRevisions, request.ConditionCodes,
                request.DecisionFingerprint, request.OpenCommitmentCorrelations, request.AttentionReviewId,
                request.Payload, (previous?.Revision ?? 0) + 1, previous?.CreatedAt ?? now, now);
        }

        Save(new(ProjectStateKeys.Portfolio("producer"), "producer.portfolio", 1, "Active", new Dictionary<string, string>(), [], "", [], Guid.Empty,
            JsonSerializer.SerializeToElement(portfolio, web), null, "seed"));
        var sessions = new Dictionary<Guid, AgentCoordinationSession>();
        var participant = new AgentCoordinationParticipant(victor, victorInstallation, "Victor", "Technical Director");
        var self = new AgentCoordinationParticipant(producer, Guid.NewGuid(), "Gabriel", "Producer");
        Runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) => Task.FromResult(workstream))
            .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(roster, project)))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(Save(r)))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(Sprints))
            .RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (r, _) => {
                var sprint = JsonSerializer.Deserialize<WorkSprint>("{}", web)! with { Id = Guid.NewGuid(), BoardId = r.BoardId, Name = r.Name, Status = "Planned", Sequence = r.Sequence };
                Sprints.Add(sprint); return Task.FromResult(sprint); })
            .RegisterCapability<object, ArtifactPackage>(PlatformCapabilities.ArtifactPackageRead, (_, _) => Task.FromResult(
                new ArtifactPackage(packageId, "Planning package", "video-game.production", 1, "Accepted", [], DateTimeOffset.UtcNow) { WorkstreamId = project, TeamId = team }))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, (request, _) => {
                Requests.Add(request);
                if (AcceptedSessions.TryGetValue(request.IdempotencyKey, out var existing))
                    return Task.FromResult(existing);
                var failed = AcceptedSessions.Count == 0;
                var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty, self, participant,
                    "Planning", "Plan", [], failed ? "Blocked" : "Active", 1, 1, victor, failed, failed ? SpecialistFailure : null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    failed ? [new(Guid.NewGuid(), 1, victor, "Blocked", SpecialistFailure, DateTimeOffset.UtcNow)] : []) { BoardSource = new(boardId), SourceKind = "Board" };
                sessions[session.Id] = session;
                AcceptedSessions.Add(request.IdempotencyKey, session);
                if (!failed && loseFirstRetryResponse)
                {
                    loseFirstRetryResponse = false;
                    throw new HttpRequestException("Coordination response lost after acceptance.");
                }
                return Task.FromResult(session);
            })
            .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,
                (r, _) => Task.FromResult(sessions[r.SessionId]));
        Identity = new AgentIdentity(producer.ToString(), "Gabriel", null, "Producer", null, [], null, director.ToString(), "Marcus");
        Card = JsonSerializer.Deserialize<PersonalTodoItem>("{}", web)! with { Id = Guid.NewGuid(), Revision = 1, BoardId = boardId, IsExecutable = true, Status = PersonalTodoStatuses.Ready,
            OwnerOrganizationUserId = producer, CorrelationId = $"producer-planning:{project:N}:{fingerprint}",
            WorkContext = new PersonalTodoWorkContext { WorkstreamId = project, TeamId = team, BoardId = boardId, SourceFingerprint = fingerprint } };
    }
}

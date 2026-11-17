using System.Reflection;
using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// Enters through the Producer's real planning commitment (HandlePersonalTodoAsync), with the platform scripted.
/// QFN Games Brickstorm, November 17: the Technical Director's roadmap generation failed, the Producer blocked
/// permanently, and asking again would have returned the same failed session.
/// </summary>
public sealed class PlanningCommitmentJourneyTests
{
    private static T Read<T>(PersonalTodoResult result, string property) =>
        (T)typeof(PersonalTodoResult).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;

    [Fact]
    public async Task A_failed_roadmap_generation_is_retried_later_with_a_fresh_request()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var producer = Guid.NewGuid(); var director = Guid.NewGuid(); var victor = Guid.NewGuid(); var victorInstallation = Guid.NewGuid();
        var project = Guid.NewGuid(); var team = Guid.NewGuid(); var boardId = Guid.NewGuid(); var packageId = Guid.NewGuid();
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
        AgentOperatingStateResponse Save(AgentOperatingStateWriteRequest r) => states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
            r.Status, r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Save(new(ProjectStateKeys.Portfolio("producer"), "producer.portfolio", 1, "Active", new Dictionary<string, string>(), [], "", [], Guid.Empty,
            JsonSerializer.SerializeToElement(portfolio, web), null, "seed"));
        var sprints = new List<WorkSprint>();
        var requests = new List<StartBoardCoordinationRequest>();
        var sessions = new Dictionary<Guid, AgentCoordinationSession>();
        var participant = new AgentCoordinationParticipant(victor, victorInstallation, "Victor", "Technical Director");
        var self = new AgentCoordinationParticipant(producer, Guid.NewGuid(), "Gabriel", "Producer");
        const string failure = "Page failed after three attempts: approved outcomes and constraints (finish=stop, characters=18092): 2 commitment(s) do not cite a valid accepted source index.";
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) => Task.FromResult(workstream))
            .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(roster, project)))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(Save(r)))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(sprints))
            .RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (r, _) => {
                var sprint = JsonSerializer.Deserialize<WorkSprint>("{}", web)! with { Id = Guid.NewGuid(), BoardId = r.BoardId, Name = r.Name, Status = "Planned", Sequence = r.Sequence };
                sprints.Add(sprint); return Task.FromResult(sprint); })
            .RegisterCapability<object, ArtifactPackage>(PlatformCapabilities.ArtifactPackageRead, (_, _) => Task.FromResult(
                new ArtifactPackage(packageId, "Planning package", "video-game.production", 1, "Accepted", [], DateTimeOffset.UtcNow) { WorkstreamId = project, TeamId = team }))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, (request, _) => {
                requests.Add(request);
                var failed = requests.Select(r => r.IdempotencyKey).Distinct().Count() == 1;
                var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty, self, participant,
                    "Planning", "Plan", [], failed ? "Blocked" : "Active", 1, 1, victor, failed, failed ? failure : null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    failed ? [new(Guid.NewGuid(), 1, victor, "Blocked", failure, DateTimeOffset.UtcNow)] : []) { BoardSource = new(boardId), SourceKind = "Board" };
                sessions[session.Id] = session;
                return Task.FromResult(session);
            })
            .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,
                (r, _) => Task.FromResult(sessions[r.SessionId]));
        var context = runtime.CreateContext(Guid.NewGuid().ToString(), identity: new AgentIdentity(producer.ToString(), "Gabriel", null, "Producer", null, [], null, director.ToString(), "Marcus"));
        var commitment = JsonSerializer.Deserialize<PersonalTodoItem>("{}", web)! with { Id = Guid.NewGuid(), Revision = 1, Status = PersonalTodoStatuses.Running,
            OwnerOrganizationUserId = producer, CorrelationId = $"producer-planning:{project:N}:{fingerprint}",
            WorkContext = new PersonalTodoWorkContext { WorkstreamId = project, TeamId = team, BoardId = boardId, SourceFingerprint = fingerprint } };
        var agent = new SpecialistAgent();

        var first = await agent.HandlePersonalTodoAsync(commitment, context, default);
        // A failed generation is a retry with the specialist's exact diagnostic, never a permanent block.
        Assert.Contains("2 commitment(s) do not cite", Read<string>(first, "RetryFailure"));
        Assert.Single(requests);

        var second = await agent.HandlePersonalTodoAsync(commitment, context, default);
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].IdempotencyKey, requests[1].IdempotencyKey); // A fresh generation, not the failed session again.
        Assert.NotNull(Read<DateTimeOffset?>(second, "NextReviewAt"));
        Assert.Contains("Waiting for both role authorities", Read<string>(second, "Content"));
        Assert.Single(sprints); // The provisional sprint was not duplicated by the retry.
    }
}

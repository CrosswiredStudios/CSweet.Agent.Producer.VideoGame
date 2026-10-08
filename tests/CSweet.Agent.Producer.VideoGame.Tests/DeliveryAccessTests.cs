using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// Prism Break (2026-10-08): the platform denied the Producer work.delivery.read.v1, the recovery commitment crashed on
/// every review, and the whole project stalled behind "execution stopped unexpectedly" with nobody told why.
/// </summary>
public sealed class DeliveryAccessTests
{
    [Fact]
    public void The_escalation_names_the_refusal_and_follows_the_relay_convention()
    {
        var board = Guid.NewGuid();
        var (key, content) = SpecialistAgent.DeliveryAccessEscalation("Prism Break", board, "work.delivery.read.v1",
            "The project delivery grant is required: work.delivery.read.v1");

        Assert.Contains("Prism Break delivery can't start", content);
        Assert.Contains("`work.delivery.read.v1`", content);
        Assert.Contains("Platform reason: The project delivery grant is required", content);
        Assert.Contains("Projects → Prism Break → Manage members", content);
        Assert.Contains("`Retry staffing: <what changed>`", content);
        Assert.Equal(key, SpecialistAgent.DeliveryAccessEscalation("Prism Break", board, "work.delivery.read.v1", "other wording").Key);
        Assert.NotEqual(key, SpecialistAgent.DeliveryAccessEscalation("Prism Break", board, "work.delivery.control.v1", "").Key);
    }

    [Theory]
    [InlineData("{\"code\":\"Denied\",\"message\":\"The project delivery grant is required: work.delivery.read.v1\"}",
        "The project delivery grant is required: work.delivery.read.v1")]
    [InlineData("Plain refusal.", "Plain refusal.")]
    [InlineData("{not json", "{not json")]
    public void The_platform_reason_is_read_from_its_envelope(string message, string expected) =>
        Assert.Equal(expected, SpecialistAgent.RefusalReason(
            new PlatformCapabilityException("work.delivery.read.v1", PlatformCapabilityErrorCode.Denied, message)));

    [Theory]
    [InlineData("Retry staffing: added Gabriel to Prism Break", true)]
    [InlineData("retry staffing:   access fixed  ", true)]
    [InlineData("Retry staffing:", false)]
    [InlineData("Please retry staffing: now", false)]
    [InlineData("Retry ticket VG-1: fixed", false)]
    public void Only_an_explicit_staffing_retry_is_a_command(string message, bool expected) =>
        Assert.Equal(expected, SpecialistAgent.TryReadStaffingRetry(message, out _));

    [Fact]
    public async Task A_denied_delivery_capability_waits_and_escalates_once_instead_of_failing()
    {
        var h = new Harness();

        var first = await new SpecialistAgent().HandlePersonalTodoAsync(h.Commitment, h.Context, default);
        var second = await new SpecialistAgent().HandlePersonalTodoAsync(h.Commitment, h.Context, default);

        foreach (var result in new[] { first, second })
        {
            Assert.NotNull(Read<DateTimeOffset?>(result, "NextReviewAt"));
            Assert.Contains("Waiting for project access", Read<string>(result, "Content"));
            Assert.Contains(WorkOrchestrationCapabilities.ConfigureProfile, Read<string>(result, "Content"));
        }
        var message = Assert.Single(h.Messages);
        Assert.StartsWith("producer-decision:", message.Key);
        Assert.Contains("Prism Break delivery can't start", message.Value);
        Assert.Contains(WorkOrchestrationCapabilities.ConfigureProfile, message.Value);
        Assert.Equal(h.Director, h.Recipient);
    }

    private static T Read<T>(PersonalTodoResult result, string property) =>
        (T)typeof(PersonalTodoResult).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;

    private sealed class Harness
    {
        private readonly Guid producer = Guid.NewGuid(), chat = Guid.NewGuid();
        private readonly Dictionary<string, AgentOperatingStateResponse> states = [];
        public readonly Guid Director = Guid.NewGuid();
        public readonly Dictionary<string, string> Messages = [];
        public Guid? Recipient;
        public AgentRuntimeContext Context { get; }
        public PersonalTodoItem Commitment { get; }

        public Harness()
        {
            var project = Guid.NewGuid(); var team = Guid.NewGuid(); var boardId = Guid.NewGuid();
            var board = new WorkBoardSummary(boardId, "VGPRISM", "Prism Break", false, false, 1, [])
                { TeamId = team, WorkstreamId = project, ManagerOrganizationUserId = producer };
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var workstream = JsonSerializer.Deserialize<WorkstreamDetail>(
                JsonSerializer.Serialize(new { id = project, name = "Prism Break", outcome = "Ship it", successCriteria = Array.Empty<string>(),
                    lifecycleStage = "concept", status = "Approved", accountableManagerOrganizationUserId = producer, profileDefinitionDigest = "digest" }, web), web)!;
            var roster = new AgentTeamContext(team.ToString(), "video-game-team", "Video Game Team", 1, Director.ToString(), "Naomi", [], [], 0, false);
            var runtime = new AgentTestRuntime()
                .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                    (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [])))
                .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead,
                    (_, _) => Task.FromResult(workstream))
                .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
                    (_, _) => Task.FromResult(new TeamRosterV2Response(roster, project)))
                // Profile orchestration is deliberately not registered: the test runtime denies it like the platform did.
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) =>
                    {
                        var saved = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions,
                            r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId,
                            r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                        states[r.StateKey] = saved;
                        return Task.FromResult(saved);
                    })
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (r, _) =>
                {
                    Recipient = r.GetProperty("participantOrganizationUserIds")[0].GetGuid();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        succeeded = true, message = "Created", chat = new { id = chat, isDirect = true, isPrivate = true,
                            participants = new[] { new { organizationUserId = Director, employeeType = "Human", displayName = "Naomi", role = "Member" } } }
                    }));
                })
                .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) =>
                {
                    var content = r.GetProperty("content").GetString()!;
                    Messages[r.GetProperty("idempotencyKey").GetString()!] = content;
                    return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, producer, "Gabriel", "Agent", content,
                        DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
                });
            Context = runtime.CreateContext(Guid.NewGuid().ToString(), identity: new AgentIdentity(producer.ToString(), "Gabriel", null,
                "Producer", null, [], null, Director.ToString(), "Naomi"));
            Commitment = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), producer, producer, "Gabriel",
                "Maintain delivery assignments and recover stopped tickets", "Recover", PersonalTodoStatuses.Running,
                "Critical", 1, 1, null, null, null, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            {
                CorrelationId = $"producer-workflow-recovery:{boardId:N}:0123456789abcdef",
                WorkContext = new PersonalTodoWorkContext { WorkstreamId = project, TeamId = team, BoardId = boardId }
            };
        }
    }
}

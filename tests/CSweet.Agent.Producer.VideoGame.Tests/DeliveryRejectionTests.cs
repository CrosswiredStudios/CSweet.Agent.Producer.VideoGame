using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

/// <summary>
/// Super Awesome Games (2026-11-10): the platform rejected every delivery plan configuration, and the Producer re-ran
/// the same request every few minutes without telling anyone, so Production Sprint 1 never started.
/// </summary>
public sealed class DeliveryRejectionTests
{
    private const string Reason = "Increment delivery planning needs attention: The delivery plan name is 204 characters; the limit is 160.";

    [Fact]
    public void Rejected_plans_recheck_hourly_and_other_gaps_keep_their_wait()
    {
        var normal = TimeSpan.FromMinutes(15);
        Assert.Equal(SpecialistAgent.RejectedDeliveryRecheck,
            SpecialistAgent.StaffingRecheckDelay(SpecialistAgent.PlatformRejectionGapPrefix + Reason, normal));
        Assert.Equal(normal, SpecialistAgent.StaffingRecheckDelay(Reason, normal));
        Assert.Equal(normal, SpecialistAgent.StaffingRecheckDelay(null, normal));
    }

    [Fact]
    public void The_escalation_names_the_reason_and_follows_the_relay_convention()
    {
        var board = Guid.NewGuid();
        var (key, content) = SpecialistAgent.DeliveryRejectionEscalation("Prism Break", board, Reason);

        Assert.StartsWith("producer-decision:", key);
        Assert.Contains("Prism Break delivery is stuck", content);
        Assert.Contains("Platform reason: " + Reason, content);
        Assert.Contains("`Retry staffing: <what changed>`", content);
        Assert.Contains("every 60 minutes", content);
        Assert.Equal(key, SpecialistAgent.DeliveryRejectionEscalation("Other name", board, Reason).Key);
        Assert.NotEqual(key, SpecialistAgent.DeliveryRejectionEscalation("Prism Break", board, Reason + " Changed.").Key);
        Assert.NotEqual(key, SpecialistAgent.DeliveryRejectionEscalation("Prism Break", Guid.NewGuid(), Reason).Key);
    }

    [Fact]
    public async Task A_rejection_is_escalated_to_the_manager_once()
    {
        var producer = Guid.NewGuid(); var director = Guid.NewGuid(); var chat = Guid.NewGuid(); var project = Guid.NewGuid();
        var board = new WorkBoardSummary(Guid.NewGuid(), "VGPRISM", "Prism Break", false, false, 1, [])
            { TeamId = Guid.NewGuid(), WorkstreamId = project, ManagerOrganizationUserId = producer };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var workstream = JsonSerializer.Deserialize<WorkstreamDetail>(
            JsonSerializer.Serialize(new { id = project, name = "Prism Break", outcome = "Ship it", successCriteria = Array.Empty<string>(),
                lifecycleStage = "concept", status = "Approved", accountableManagerOrganizationUserId = producer, profileDefinitionDigest = "digest" }, web), web)!;
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var messages = new Dictionary<string, string>(); Guid? recipient = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) => Task.FromResult(workstream))
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
                recipient = r.GetProperty("participantOrganizationUserIds")[0].GetGuid();
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    succeeded = true, message = "Created", chat = new { id = chat, isDirect = true, isPrivate = true,
                        participants = new[] { new { organizationUserId = director, employeeType = "Agent", displayName = "Marcus", role = "Member" } } }
                }));
            })
            .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) =>
            {
                var content = r.GetProperty("content").GetString()!;
                messages[r.GetProperty("idempotencyKey").GetString()!] = content;
                return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, producer, "Gabriel", "Agent", content,
                    DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
            });
        var context = runtime.CreateContext(Guid.NewGuid().ToString(), identity: new AgentIdentity(producer.ToString(), "Gabriel", null,
            "Producer", null, [], null, director.ToString(), "Marcus"));

        await SpecialistAgent.EscalateDeliveryRejectionAsync(board, Reason, context, default);
        await SpecialistAgent.EscalateDeliveryRejectionAsync(board, Reason, context, default);

        var message = Assert.Single(messages);
        Assert.Contains("Prism Break delivery is stuck", message.Value);
        Assert.Contains(Reason, message.Value);
        Assert.Equal(director, recipient);
    }
}

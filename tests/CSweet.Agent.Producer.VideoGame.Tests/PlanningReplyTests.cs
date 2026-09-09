using System.Text.Json;
using CSweet.Agent.SDK;
using Xunit;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningReplyTests
{
    [Fact]
    public void PlanningArtifactSharesExactAcceptedMemberAndPreservesCycle()
    {
        var cycle = new CrosswiredStudios.VideoGame.Contracts.GameProductionPlanningCycleV1(
            Guid.NewGuid(), Guid.NewGuid(), 3, Guid.NewGuid(), "profile", Guid.NewGuid(), 1,
            "package-digest", "concept", "vision-approved", "fingerprint");
        var member = new CSweet.WorkManagement.Contracts.ArtifactPackageMemberDigest(
            Guid.NewGuid(), Guid.NewGuid(), "brief", new string('a', 64));
        var artifact = SpecialistAgent.PlanningArtifact(cycle, [member]);
        Assert.Equal(cycle, artifact.Payload.Deserialize<CrosswiredStudios.VideoGame.Contracts.GameProductionPlanningCycleV1>());
        var reference = Assert.Single(artifact.Payload.GetProperty("documentReferences")
            .Deserialize<CollaborationDocumentReference[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(member.ArtifactId, reference.DocumentId);
        Assert.Equal(member.AcceptedRevisionId, reference.RevisionId);
        Assert.Equal(member.Sha256, reference.ContentSha256);
    }

    [Theory]
    [InlineData(false, "platform.artifact-package.read.v1", true)]
    [InlineData(true, "platform.artifact-package.read.v1", false)]
    [InlineData(false, "work.item.read", false)]
    public void DocumentRecoveryRequiresMissingReferencesAndPackageReadFailure(bool shared, string capability, bool expected)
    {
        var participant = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director");
        var artifact = new AgentCoordinationArtifact("video-game.production.planning-cycle.v1", "1.0", "cycle", 1, true,
            shared ? JsonSerializer.SerializeToElement(new { documentReferences = new[] { new { documentId = Guid.NewGuid() } } }) :
                JsonSerializer.SerializeToElement(new {}), "digest");
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            participant, target, "Planning", "Delivery", [], "Failed", 2, 1, null, false,
            $"agent-failure:v1;code=capability.failed;retryable=false;capability={capability};diagnosticId=test",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), 0, participant.OrganizationUserId, "Continue", "Plan", DateTimeOffset.UtcNow, artifact)])
            { SourceKind = "Board" };
        Assert.Equal(expected, SpecialistAgent.NeedsPlanningDocumentRecovery(session));
    }

    public static IEnumerable<object[]> ReplyCases()
    {
        var pairs = new[] {
            ("planning-cycle", "technical-delivery-proposal"),
            ("role-estimate-request", "role-estimate-capacity-proposal"),
            ("qa-readiness-request", "qa-sprint-readiness-assessment") };
        foreach (var (request, reply) in pairs)
        foreach (var scenario in new[] { "complete", "blocked", "wrong-key", "wrong-type", "partial", "missing-context" })
            yield return new object[] { scenario, scenario == "complete" ? "Completed" : "Blocked",
                $"video-game.production.{request}.v1", $"video-game.production.{reply}.v1" };
    }

    [Theory]
    [MemberData(nameof(ReplyCases))]
    public async Task PlanningReplyDoesNotEnterPitchRefinement(string scenario, string expected, string requestType, string replyType)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director");
        var turns = new List<AgentCoordinationTurn> {
            new(Guid.NewGuid(), 0, self.OrganizationUserId, "Continue", "Plan", DateTimeOffset.UtcNow,
                new(requestType, "1.0", "cycle", 1, true, JsonSerializer.SerializeToElement(new {}), "digest")),
            new(Guid.NewGuid(), 1, target.OrganizationUserId, scenario == "blocked" ? "Blocked" : "Completed", "Need an engine decision", DateTimeOffset.UtcNow,
                scenario == "blocked" ? null : new(scenario == "wrong-type" ? "unrelated.v1" : replyType, "1.0",
                    scenario == "wrong-key" ? "other" : "cycle", 1, scenario != "partial", JsonSerializer.SerializeToElement(new {}), "digest")) };
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 2, 2, "Planning", "Delivery", [], self, target, true, turns)
            { SourceKind = "Board", WorkContext = scenario == "missing-context" ? null :
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(request, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal(expected, result.Disposition);
        if (scenario == "blocked") Assert.Contains("Need an engine decision", result.Content);
        Assert.DoesNotContain("Pitch refinement", result.Content);
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("semantic", false)]
    [InlineData("active", false)]
    [InlineData("context-present", false)]
    [InlineData("format", false)]
    public void ContextRecoveryOnlyReplacesTheKnownLegacyFailure(string scenario, bool expected)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director");
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            self, target, "Planning", "Delivery", [], scenario == "active" ? "Active" : "Blocked", 3, 3, null, false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), 1, target.OrganizationUserId, "Blocked", scenario == "format" ? "Technical planning returned invalid JSON; revise the proposal." : scenario == "semantic" ? "Need an engine decision" :
                "Planning requires the exact workstream and planning fingerprint.", DateTimeOffset.UtcNow)])
            { SourceKind = "Board", WorkContext = scenario == "context-present" ?
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) : null };
        Assert.Equal(expected, SpecialistAgent.NeedsPlanningContextRecovery(session));
        Assert.Equal(scenario == "format", SpecialistAgent.NeedsPlanningFormatRecovery(session));
    }
}


using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class RoleReplanningCorrectionTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;

    private static (RoleRepairRequest Request, GameTechnicalDeliveryProposalV1 Proposal, AgentCoordinationSession Session) Fixture(bool valid = false)
    {
        var (request, items) = RoleReplanningTests.Fixture();
        if (!valid)
        {
            items[0] = items[0] with { AcceptanceCriteria = ["Reworded plan"] };
            items[1] = items[1] with { Description = "Shortened implementation", AcceptanceCriteria = ["Working prototype"] };
            items[2] = items[2] with { DependencyProposalKeys = ["plan"] };
        }
        var cycle = new GameProductionPlanningCycleV1(request.WorkstreamId, request.TeamId, 1, request.BoardId,
            "profile", Guid.NewGuid(), 1, "package", "production", "game", "fingerprint");
        var proposal = new GameTechnicalDeliveryProposalV1(cycle, items, [], valid ? ["60 fps"] : [], [], "digest");
        var artifact = Empty<AgentCoordinationArtifact>() with { Type = "video-game.production.technical-delivery-proposal.v1",
            Key = cycle.PlanningFingerprint, Payload = JsonSerializer.SerializeToElement(proposal) };
        var session = Empty<AgentCoordinationSession>() with { Id = Guid.NewGuid(), Status = "Completed",
            Target = new(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director"),
            Turns = [Empty<AgentCoordinationTurn>() with { Artifact = artifact }] };
        return (request, proposal, session);
    }

    [Fact]
    public void StructuredRepairRetainsAcceptedDocumentReferencesAndExactCycle()
    {
        var (request, proposal, _) = Fixture();
        var member = new CSweet.WorkManagement.Contracts.ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", new string('a', 64));
        var artifact = SpecialistAgent.RoleRepairArtifact(proposal.Cycle, [member], request);
        Assert.Equal(proposal.Cycle, artifact.Payload.Deserialize<GameProductionPlanningCycleV1>());
        var reference = Assert.Single(artifact.Payload.GetProperty("documentReferences")
            .Deserialize<CollaborationDocumentReference[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(member.ArtifactId, reference.DocumentId);
        Assert.Equal(member.AcceptedRevisionId, reference.RevisionId);
        Assert.Equal(member.Sha256, reference.ContentSha256);
    }
    [Fact]
    public void CorrectionContainsAllExactOmissionsAndDependencyGaps()
    {
        var (request, proposal, _) = Fixture();
        var findings = SpecialistAgent.RoleRepairCorrectionFindings(request, proposal);
        Assert.Contains(findings, x => x.Contains("Plan and implement the foundation"));
        Assert.Contains(findings, x => x.Contains("Wave template committed"));
        Assert.DoesNotContain(findings, x => x.Contains("Retain this exact technical constraint"));
        Assert.Contains(findings, x => x.Contains("game: add a direct or transitive dependency on prototype"));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateRoleRepairCoverage(request, proposal.DeliveryItems, proposal.TechnicalConstraints));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Completed")]
    [InlineData("Blocked")]
    public async Task LostResponseAndRepeatedReviewsReuseOneCorrectionEvenWhenTerminal(string correctionStatus)
    {
        var (request, proposal, original) = Fixture();
        var correction = original with { Id = Guid.NewGuid(), Status = correctionStatus };
        var calls = new List<StartBoardCoordinationRequest>();
        var runtime = new AgentTestRuntime().RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(
            CommunicationCapabilities.CoordinationStartBoard, (start, _) => {
                calls.Add(start);
                if (calls.Count == 1) throw new IOException("Reply lost after durable creation");
                return Task.FromResult(correction);
            });
        Task<AgentCoordinationSession> Run() => SpecialistAgent.EnsureRoleRepairCorrectionAsync(request, original,
            proposal.Cycle, [], ["Keep the accepted scope"], runtime.CreateContext(), default);
        await Assert.ThrowsAsync<IOException>(Run);
        var first = await Run();
        var second = await Run();
        Assert.Equal(correction.Id, first.Id);
        Assert.Equal(correction.Id, second.Id);
        Assert.Equal(correctionStatus, second.Status);
        Assert.Single(calls.Select(x => x.IdempotencyKey).Distinct());
        Assert.Equal($"producer-role-repair-correction:{original.Id:N}:structured-v1", calls[0].IdempotencyKey);
        Assert.All(calls, x => {
            Assert.Equal(original.Target.OrganizationUserId, x.TargetOrganizationUserId);
            Assert.Equal(request.BoardId, x.BoardId);
            Assert.Equal(proposal.Cycle.PlanningFingerprint, x.Artifact!.Key);
            var repair = x.Artifact.Payload.GetProperty("roleRepair");
            Assert.Equal(1, repair.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(request.WorkItemId, repair.GetProperty("sourceWorkItemId").GetGuid());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                request.OriginalItems.Single(i => i.Id == request.WorkItemId).Planning))).ToLowerInvariant(),
                repair.GetProperty("sourcePlanningSha256").GetString());
            Assert.Equal(proposal.Cycle, x.Artifact.Payload.Deserialize<GameProductionPlanningCycleV1>());
            Assert.Contains("Wave template committed", x.InitialMessage);
            Assert.Contains("Keep the accepted scope", x.InitialMessage);
            Assert.InRange(x.InitialMessage.Length, 1, 32768);
        });
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Failed", false)]
    [InlineData("Completed", true)]
    public async Task PendingFailedOrValidOriginalDoesNotCreateCorrection(string status, bool valid)
    {
        var (request, proposal, original) = Fixture(valid);
        original = original with { Status = status };
        Assert.Same(original, await SpecialistAgent.EnsureRoleRepairCorrectionAsync(request, original, proposal.Cycle,
            [], [], new AgentTestRuntime().CreateContext(), default));
    }

    [Fact]
    public void MixedTicketCriteriaNotFlaggedByEarlierReviewStillMoveOutOfPlanning()
    {
        var (request, proposal, _) = Fixture(true);
        Assert.DoesNotContain("Wave template committed", request.RoleRepairCriteria);
        var items = proposal.DeliveryItems.ToArray();
        items[0] = items[0] with { AcceptanceCriteria = ["Wave template committed"] };
        items[1] = items[1] with { AcceptanceCriteria = ["Working prototype"] };
        var findings = SpecialistAgent.RoleRepairCorrectionFindings(request, proposal with { DeliveryItems = items });
        Assert.Contains(findings, x => x.Contains("plan: move this execution criterion entirely") && x.Contains("Wave template committed"));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateRoleRepairCoverage(request, items, ["60 fps"]));
    }

    [Fact]
    public void OriginalConstraintsAreRetainedWithoutDependingOnModelTranscription()
    {
        var (request, proposal, _) = Fixture(true);
        proposal = proposal with { TechnicalConstraints = ["60 fps each"] };
        Assert.Empty(SpecialistAgent.RoleRepairCorrectionFindings(request, proposal));
        Assert.Equal(new[] { "60 fps" }, SpecialistAgent.RetainedRoleRepairConstraints(request));
        Assert.Empty(SpecialistAgent.RetainedRoleRepairConstraints(null));
        Assert.Equal(new[] { "Working prototype" }, request.RoleRepairCriteria);
    }
    [Fact]
    public async Task OversizedCorrectionBlocksWithoutDroppingEvidenceOrDispatching()
    {
        var (request, proposal, original) = Fixture();
        request = request with { Findings = [new string('x', 65536)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecialistAgent.EnsureRoleRepairCorrectionAsync(
            request, original, proposal.Cycle, [], [], new AgentTestRuntime().CreateContext(), default));
    }
}

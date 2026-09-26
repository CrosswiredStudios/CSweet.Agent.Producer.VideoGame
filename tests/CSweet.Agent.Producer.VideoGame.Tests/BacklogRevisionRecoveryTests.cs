using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class BacklogRevisionRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Published_backlog_reconciles_corrected_planning_and_assignment_atomically(bool eligible)
    {
        var boardId = Guid.NewGuid();
        var key = "NC-T-CONTENT-ENG";
        var ownerId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var oldAssignment = new WorkStageAssignment("specialist-execution", "AgentInstallation", ownerId, installationId)
        {
            Requirements = new("game-engineer", ["gameplay-programming"], [], ["work.execution.run.v1"]),
            SelectionEvidence = new(installationId, 1, new string('p', 64), ["gameplay-programming"], "old", DateTimeOffset.UtcNow)
        };
        var oldItem = new WorkItem(Guid.NewGuid(), boardId, null, null, "Task", "[" + key + "] Content", "Build content",
            WorkStatuses.Ready, WorkPriorities.High, null, 1, 1, null)
        {
            TypeKey = VideoGameWorkItemTypeKeys.Task,
            Planning = new WorkItemPlanningSpecification(["Build content"], ["Old incomplete criteria"]),
            StageAssignments = [oldAssignment],
            AccountableOrganizationUserId = ownerId,
            // An earlier partial reconciliation may already have copied this digest.
            ProposalProvenance = new(Guid.NewGuid(), new string('d', 64), key)
        };
        var foundation = oldItem with
        {
            Id = Guid.NewGuid(), Title = "[foundation] Foundation", Status = WorkStatuses.Completed,
            ProposalProvenance = new(Guid.NewGuid(), "older-foundation", "foundation")
        };
        var item = oldItem;
        var board = new WorkBoardSummary(boardId, "NC", "Neon Cascade", false, false, 1, []);
        var revisions = new List<ReviseWorkItemPlanningRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [item, foundation])))
            .RegisterCapability<ReviseWorkItemPlanningRequest, WorkItem>(WorkItemCapabilities.RevisePlanning,
                (request, _) => {
                    Assert.InRange(request.IdempotencyKey.Length, 1, 160);
                    // Mirror the platform's same-mutation delegation check: the old assignment
                    // cannot be submitted alongside the newly required skill.
                    var recommendation = Assert.Single(request.Planning.DelegationRecommendations);
                    foreach (var assignment in request.StageAssignments!)
                        Assert.All(recommendation.RequiredSpecializationKeys,
                            skill => Assert.Contains(skill, assignment.Requirements!.RequiredSpecializationKeys));
                    revisions.Add(request);
                    item = item with { Revision = item.Revision + 1, Planning = request.Planning,
                        ProposalProvenance = request.ProposalProvenance, Title = request.Title,
                        Description = request.Description ?? string.Empty, ParentItemId = request.ParentItemId,
                        StageAssignments = request.StageAssignments!, AccountableOrganizationUserId = request.AccountableOrganizationUserId };
                    return Task.FromResult(item);
                });
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, boardId,
            new string('p', 64), Guid.NewGuid(), 1, new string('b', 64), "concept", "vision-approved", new string('c', 64));
        var person = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            person, person, "Planning", "Plan", [], "Completed", 3, 3, null, true, "Complete",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var artifact = new AgentCoordinationArtifact("video-game.production.technical-delivery-proposal.v1", "1.0", "plan", 1, true,
            JsonSerializer.SerializeToElement(new {}), new string('d', 64));
        var proposal = new GameProposedWorkItemV1(key, VideoGameWorkItemTypeKeys.Task, "Updated content", "Build the corrected content",
            ["Corrected behavior is verified"], "game-engineer", ["engine-integration"], [], [], ["foundation"]);
        var technical = new GameTechnicalDeliveryProposalV1(cycle, [proposal, proposal with {
            ProposalKey = "foundation", DependencyProposalKeys = [] }], [], ["Current technical constraint"], [], artifact.Digest);
        var designer = new GameDesignerBacklogProposalV1(cycle, [], [], [], artifact.Digest);
        var teammate = new AgentTeammate(ownerId.ToString(), "Engineer", "Agent", "game-engineer", "game-engineer", "Peer", "Online")
        {
            AgentInstallationId = installationId, RuntimeEligibility = "Eligible", DeclaredRoleKeys = ["game-engineer"],
            EffectiveCapabilities = ["work.execution.run.v1"],
            SpecializationKeys = eligible ? ["gameplay-programming", "engine-integration"] : ["gameplay-programming"]
        };
        var roster = new AgentTeamContext(cycle.TeamId.ToString(), "team", "Team", 1, person.OrganizationUserId.ToString(), "Producer", [teammate], [], 1, false);
        for (var replay = 0; replay < 2; replay++)
            Assert.Equal(2, await SpecialistAgent.PublishCanonicalBacklogAsync(boardId, roster, cycle, [], session, artifact,
                designer, session, artifact, technical, runtime.CreateContext(), default));
        Assert.Single(revisions);
        Assert.Equal(oldItem.Id, item.Id);
        Assert.Equal(artifact.Digest, item.ProposalProvenance!.ArtifactDigest);
        Assert.Equal("[NC-T-CONTENT-ENG] Updated content", item.Title);
        Assert.Equal(proposal.Description, item.Description);
        Assert.Equal(proposal.AcceptanceCriteria, item.Planning!.AcceptanceCriteria);
        Assert.Equal(new[] { proposal.Description }, item.Planning.Requirements);
        Assert.Equal(cycle.ApprovedPackageDigest, item.Planning.ArtifactPackageDigest!.Sha256);
        Assert.Equal(new[] { foundation.Id }, item.Planning.DependencyItemIds);
        Assert.Equal(technical.TechnicalConstraints, item.Planning.Constraints);
        if (eligible)
        {
            Assert.Equal(installationId, Assert.Single(item.StageAssignments).AgentInstallationId);
            Assert.Equal(ownerId, item.AccountableOrganizationUserId);
        }
        else
        {
            Assert.Empty(item.StageAssignments);
            Assert.Null(item.AccountableOrganizationUserId);
        }
    }

    [Fact]
    public void Long_keys_preserve_all_identity_inputs_and_existing_valid_receipts()
    {
        var valid = new string('v', 160);
        Assert.Equal(valid, SpecialistAgent.BoundedMutationKey(valid));
        var oversized = "producer-hierarchy:" + new string('a', 64) + ":" + new string('b', 64) + ":NC-T-CONTENT-ENG";
        var key = SpecialistAgent.BoundedMutationKey(oversized);
        Assert.InRange(key.Length, 1, 160);
        Assert.Equal(key, SpecialistAgent.BoundedMutationKey(oversized));
        Assert.NotEqual(key, SpecialistAgent.BoundedMutationKey(oversized + "-2"));
        Assert.NotEqual(key, SpecialistAgent.BoundedMutationKey(oversized.Replace(new string('b', 64), new string('c', 64))));
    }
}

using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class BacklogRevisionRecoveryTests
{
    [Fact]
    public async Task Published_backlog_can_reconcile_full_digests_without_duplicate_tickets()
    {
        var boardId = Guid.NewGuid();
        var key = "NC-T-CONTENT-ENG";
        var oldItem = new WorkItem(Guid.NewGuid(), boardId, null, null, "Task", "[" + key + "] Content", "Build content",
            WorkStatuses.Ready, WorkPriorities.High, null, 1, 1, null)
        {
            TypeKey = VideoGameWorkItemTypeKeys.Task,
            Planning = new WorkItemPlanningSpecification(["Build content"], ["Content playable"]),
            ProposalProvenance = new(Guid.NewGuid(), new string('a', 64), key)
        };
        var item = oldItem;
        var board = new WorkBoardSummary(boardId, "NC", "Neon Cascade", false, false, 1, []);
        var revisions = new List<ReviseWorkItemPlanningRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [item])))
            .RegisterCapability<ReviseWorkItemPlanningRequest, WorkItem>(WorkItemCapabilities.RevisePlanning,
                (request, _) => {
                    Assert.InRange(request.IdempotencyKey.Length, 1, 160);
                    revisions.Add(request);
                    item = item with { Revision = item.Revision + 1, Planning = request.Planning,
                        ProposalProvenance = request.ProposalProvenance };
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
        var proposal = new GameProposedWorkItemV1(key, VideoGameWorkItemTypeKeys.Task, "Content", "Build content",
            ["Content playable"], "game-engineer", [], [], [], []);
        var technical = new GameTechnicalDeliveryProposalV1(cycle, [proposal], [], [], [], artifact.Digest);
        var designer = new GameDesignerBacklogProposalV1(cycle, [], [], [], artifact.Digest);
        var roster = new AgentTeamContext(cycle.TeamId.ToString(), "team", "Team", 1, person.OrganizationUserId.ToString(), "Producer", [], [], 0, false);
        for (var replay = 0; replay < 2; replay++)
            Assert.Equal(1, await SpecialistAgent.PublishCanonicalBacklogAsync(boardId, roster, cycle, [], session, artifact,
                designer, session, artifact, technical, runtime.CreateContext(), default));
        Assert.Single(revisions);
        Assert.Equal(oldItem.Id, item.Id);
        Assert.Equal(artifact.Digest, item.ProposalProvenance!.ArtifactDigest);
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

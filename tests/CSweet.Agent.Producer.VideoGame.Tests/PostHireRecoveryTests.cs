using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PostHireRecoveryTests
{
    [Fact]
    public void NewHireRefreshesRetainedAssignmentsAndAddsMissingStageWithoutChurningOwners()
    {
        var engineer = Member("game-engineer");
        var qa = Member("game-quality-assurance");
        var prior = Assignment(engineer, "specialist-execution", 3);
        var item = Item(prior) with { Planning = new([], ["Pass"], []) {
            DelegationRecommendations = [new("quality", "game-quality-assurance", ["work.execution.run.v1"], null, true, "QA")]
        }};
        var roster = Roster(12, engineer, qa, Member("game-engineer"));
        var assigned = SpecialistAgent.RefreshAssignments(item, roster, "profile");
        Assert.Equal(2, assigned.Count);
        Assert.Equal(engineer.AgentInstallationId, assigned.Single(x => x.StageKey == "specialist-execution").AgentInstallationId);
        Assert.Equal(qa.AgentInstallationId, assigned.Single(x => x.StageKey == "quality").AgentInstallationId);
        Assert.All(assigned, x => Assert.Equal(12, x.SelectionEvidence!.TeamRosterRevision));
        Assert.NotEqual(prior.SelectionEvidence!.DecisionFingerprint, assigned[0].SelectionEvidence!.DecisionFingerprint);
        var replay = SpecialistAgent.RefreshAssignments(item with { StageAssignments = assigned }, roster, "profile");
        Assert.Equal(assigned, replay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedOrIneligibleOwnerIsNotRubberStamped(bool ineligible)
    {
        var engineer = Member("game-engineer");
        var item = Item(Assignment(engineer, "specialist-execution", 3));
        var roster = Roster(12, ineligible ? [engineer with { EffectiveCapabilities = [] }] : []);
        Assert.Empty(SpecialistAgent.RefreshAssignments(item, roster, "profile"));
    }

    [Fact]
    public void EligibleReplacementGetsFreshEvidence()
    {
        var engineer = Member("game-engineer");
        var replacement = Member("game-engineer");
        var item = Item(Assignment(engineer, "specialist-execution", 3));
        var assigned = Assert.Single(SpecialistAgent.RefreshAssignments(item, Roster(12, replacement), "new-profile"));
        Assert.Equal(replacement.AgentInstallationId, assigned.AgentInstallationId);
        Assert.Equal("new-profile", assigned.SelectionEvidence!.ProfileDefinitionDigest);
    }

    [Theory]
    [InlineData(true, "Completed", true)]
    [InlineData(false, "Completed", false)]
    [InlineData(true, "Blocked", false)]
    [InlineData(true, "Active", false)]
    public void OnlyCompletedParentlessDesignerProposalNeedsCorrection(bool orphan, string status, bool expected)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var target = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Designer", "Designer");
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 12, Guid.NewGuid(),
            "profile", Guid.NewGuid(), 1, "digest", "concept", "vision-approved", "cycle");
        var feature = new GameProposedWorkItemV1("feature", VideoGameWorkItemTypeKeys.Feature,
            "Core loop", "Accepted loop", ["Demonstrable"], "", [], [], [], []) {
                ParentProposalKey = orphan ? null : "milestone"
            };
        var proposal = new GameDesignerBacklogProposalV1(cycle, [feature], [], [], "digest");
        var artifact = new AgentCoordinationArtifact("video-game.production.designer-backlog-proposal.v1",
            "1.0", "cycle", 1, true, JsonSerializer.SerializeToElement(proposal), "digest");
        var session = new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty,
            self, target, "Plan", "Plan", [], status, 2, 2, null, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), 1, target.OrganizationUserId, "Completed", "Proposal", DateTimeOffset.UtcNow, artifact)]) {
                SourceKind = "Board"
            };
        Assert.Equal(expected, SpecialistAgent.NeedsDesignerHierarchyRecovery(session));
    }

    private static AgentTeammate Member(string role) => new(Guid.NewGuid().ToString(), role, "Agent", role, role, "Peer", "Online") {
        AgentInstallationId = Guid.NewGuid(), RuntimeEligibility = "Eligible", DeclaredRoleKeys = [role],
        EffectiveCapabilities = ["work.execution.run.v1"]
    };
    private static AgentTeamContext Roster(long revision, params AgentTeammate[] members) =>
        new(Guid.NewGuid().ToString(), "game", "Game", revision, "", "", members, [], members.Length, false);
    private static WorkStageAssignment Assignment(AgentTeammate member, string stage, long revision) =>
        new(stage, "AgentInstallation", Guid.Parse(member.EmployeeId), member.AgentInstallationId) {
            Requirements = new(member.DeclaredRoleKeys.Single(), [], [], ["work.execution.run.v1"]),
            SelectionEvidence = new(member.AgentInstallationId!.Value, revision, "profile", [], "old", DateTimeOffset.UtcNow)
        };
    private static WorkItem Item(WorkStageAssignment assignment) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, null, "Task", "Implement", "Accepted scope", "Backlog", "High", null, 0, 1, null) {
            Planning = new([], ["Pass"], []), StageAssignments = [assignment]
        };
}

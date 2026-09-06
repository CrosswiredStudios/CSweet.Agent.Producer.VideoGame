using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ProducerPolicyTests
{
    [Fact]
    public void Phase_is_derived_from_authoritative_lifecycle_and_gate_evidence()
    {
        var workstream = Workstream("concept");
        Assert.Equal(ProducerManagementPhase.HandoffAndCharterValidation,
            ProducerPhaseResolver.Derive(workstream, []));

        var gate = new WorkstreamGateSummary(Guid.NewGuid(), workstream.Id, "vision-approved", "Vision",
            "concept", WorkstreamGateStatuses.Approved, 2, null);
        Assert.Equal(ProducerManagementPhase.ProductionPlanAndTeamReadiness,
            ProducerPhaseResolver.Derive(workstream, [gate]));
    }

    [Fact]
    public void Vacant_profile_role_can_be_proposed_immediately()
    {
        var result = ProducerCapacityPolicy.Evaluate("game-engineer", true, Team(), null, null);
        Assert.Equal("ProposeCapacity", result.Disposition);
        Assert.Equal(1, result.CapacityDelta);
    }

    [Fact]
    public void Growth_requires_samples_sustained_pressure_forecast_and_corroboration()
    {
        var principal = Principal(completed: 12, pending: 8, p85: 30, blocked: 0, blockedHours: 0, rework: 2);
        var previous = new ProducerMetricSnapshot(Guid.NewGuid(), Guid.NewGuid(), "previous",
            DateTimeOffset.UtcNow.AddDays(-7), Team(overCapacity: true),
            [principal with { P85CycleTimeHours = 20 }], []);
        var result = ProducerCapacityPolicy.Evaluate("game-engineer", false,
            Team(overCapacity: true, projected: 3, remaining: 20), principal, previous);
        Assert.Equal("ProposeCapacity", result.Disposition);
    }

    [Fact]
    public void Rework_pressure_creates_improvement_commitment_not_hiring()
    {
        var principal = Principal(completed: 20, pending: 10, p85: 40, blocked: 2, blockedHours: 20, rework: 35);
        var result = ProducerCapacityPolicy.Evaluate("game-engineer", false,
            Team(overCapacity: true, projected: 4, remaining: 50), principal,
            new ProducerMetricSnapshot(Guid.NewGuid(), Guid.NewGuid(), "prior", DateTimeOffset.UtcNow,
                Team(overCapacity: true), [principal], []));
        Assert.Equal("ImprovementCommitment", result.Disposition);
    }

    [Fact]
    public void Planning_fingerprint_changes_only_with_authoritative_planning_inputs()
    {
        var workstreamId = Guid.NewGuid();
        var first = ProducerPolicyFingerprint.ForPlanning(workstreamId, 3, new string('a', 64), new string('b', 64));
        var replay = ProducerPolicyFingerprint.ForPlanning(workstreamId, 3, new string('a', 64), new string('b', 64));
        var changedRoster = ProducerPolicyFingerprint.ForPlanning(workstreamId, 4, new string('a', 64), new string('b', 64));

        Assert.Equal(first, replay);
        Assert.Equal(first, changedRoster);
        Assert.NotEqual(first, ProducerPolicyFingerprint.ForPlanning(workstreamId, 4, new string('a', 64), new string('c', 64)));
        Assert.Equal(64, first.Length);
    }

    private static WorkstreamDetail Workstream(string phase) => new(
        Guid.NewGuid(), "Game", "Ship", [], phase, "Active", Guid.NewGuid(), null, null, null,
        "video-game-production.v2", 2, null, "digest", 1);

    private static WorkFlowTeamMetrics Team(bool overCapacity = false, decimal projected = 1, decimal remaining = 0) =>
        new(2, 10, 90, 10, 20, 5, 10, 20, 2, 8, 0, 0, 0, 0, 0, 5, remaining, 10, projected, overCapacity);

    private static WorkFlowPrincipalMetrics Principal(
        int completed, int pending, decimal p85, int blocked, decimal blockedHours, decimal rework) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "game-engineer", completed, completed, 4, 10, p85,
            1, pending, blocked, blockedHours, 0, rework, []);
}

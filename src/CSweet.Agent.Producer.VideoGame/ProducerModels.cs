using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public enum ProducerManagementPhase
{
    HandoffAndCharterValidation = 1,
    ProductionPlanAndTeamReadiness = 2,
    BoardPipelineAndSprintReadiness = 3,
    PrototypeAndVerticalSliceDelivery = 4,
    ProductionCadenceAndMilestoneControl = 5,
    AlphaBetaAndReleaseReadiness = 6,
    LaunchAndStabilization = 7,
    LiveOperationsRetrospectiveAndClosure = 8
}

public sealed record ProducerMetricSnapshot(
    Guid WorkstreamId,
    Guid BoardId,
    string SourceRevision,
    DateTimeOffset CapturedAt,
    WorkFlowTeamMetrics Team,
    IReadOnlyList<WorkFlowPrincipalMetrics> Principals,
    IReadOnlyList<string> ConditionCodes);

public sealed record ProducerOperatingState
{
    public ProducerManagementPhase Phase { get; init; } = ProducerManagementPhase.HandoffAndCharterValidation;
    public IReadOnlyDictionary<Guid, ProducerManagementPhase> WorkstreamPhases { get; init; } =
        new Dictionary<Guid, ProducerManagementPhase>();
    public IReadOnlyDictionary<Guid, string> AcceptedVisionDigests { get; init; } =
        new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, ProducerAcceptedHandoff> AcceptedHandoffs { get; init; } =
        new Dictionary<Guid, ProducerAcceptedHandoff>();
    public IReadOnlyDictionary<Guid, ProducerPlanningCycleState> PlanningCycles { get; init; } =
        new Dictionary<Guid, ProducerPlanningCycleState>();
    public IReadOnlyList<ProducerMetricSnapshot> MetricSnapshots { get; init; } = [];
    public IReadOnlyList<string> OpenRisks { get; init; } = [];
    public IReadOnlyList<string> PhaseCommitments { get; init; } = [];
    public IReadOnlyList<string> DecisionFingerprints { get; init; } = [];
    public ProducerCommitmentSnapshot PersonalCommitments { get; init; } = new();
    public DateTimeOffset? LastWeeklyDigestAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProducerCommitmentSnapshot(
    int Open = 0,
    int Ready = 0,
    int Running = 0,
    int Blocked = 0,
    int Waiting = 0,
    int Aging = 0);

public sealed record ProducerAcceptedHandoff(
    Guid WorkstreamId,
    Guid ArtifactId,
    Guid AcceptedRevisionId,
    string RevisionDigest,
    string HandoffDigest,
    Guid CoordinationSessionId,
    Guid PlanningPackageId,
    int PlanningPackageVersion,
    DateTimeOffset AcceptedAt);

public sealed record ProducerPlanningCycleState(
    Guid WorkstreamId,
    Guid BoardId,
    Guid TeamId,
    string PlanningFingerprint,
    Guid? ArtifactPackageId,
    Guid? DesignerSessionId,
    Guid? TechnicalDirectorSessionId,
    string? DesignerProposalDigest,
    string? TechnicalProposalDigest,
    string? ReconciledDigest,
    DateTimeOffset UpdatedAt);

public static class ProducerPhaseResolver
{
    public static ProducerManagementPhase Derive(
        WorkstreamDetail workstream,
        IReadOnlyCollection<WorkstreamGateSummary> gates)
    {
        static bool Approved(IReadOnlyCollection<WorkstreamGateSummary> values, string key) =>
            values.Any(x => x.Key == key && x.Status == WorkstreamGateStatuses.Approved);

        return workstream.LifecycleStage switch
        {
            "intake" => ProducerManagementPhase.HandoffAndCharterValidation,
            "concept" when !Approved(gates, "vision-approved") => ProducerManagementPhase.HandoffAndCharterValidation,
            "concept" => ProducerManagementPhase.ProductionPlanAndTeamReadiness,
            "pre-production" when !Approved(gates, "pre-production-ready") =>
                ProducerManagementPhase.ProductionPlanAndTeamReadiness,
            "pre-production" => ProducerManagementPhase.BoardPipelineAndSprintReadiness,
            "prototype" or "vertical-slice" => ProducerManagementPhase.PrototypeAndVerticalSliceDelivery,
            "production" => ProducerManagementPhase.ProductionCadenceAndMilestoneControl,
            "alpha" or "beta" or "release-candidate" => ProducerManagementPhase.AlphaBetaAndReleaseReadiness,
            "launch" or "post-launch-stabilization" => ProducerManagementPhase.LaunchAndStabilization,
            "live-operations" or "closure" => ProducerManagementPhase.LiveOperationsRetrospectiveAndClosure,
            _ => ProducerManagementPhase.HandoffAndCharterValidation
        };
    }
}

public static class ProducerCapacityPolicy
{
    public static ProducerCapacityAssessment Evaluate(
        string roleKey,
        bool requiredRoleVacant,
        WorkFlowTeamMetrics team,
        WorkFlowPrincipalMetrics? role,
        ProducerMetricSnapshot? previous,
        bool dependencyDelay = false,
        bool toolingFailure = false)
    {
        if (requiredRoleVacant)
            return Proposal(roleKey, "Profile-required role is vacant.", "vacancy", team, role);
        if (role is null || team.CompletedSprintCount < 2 || role.CompletedStageCount < 10)
            return new("InsufficientData", roleKey, null,
                "Capacity growth requires two completed sprints and ten attributed completed stages.",
                Fingerprint(roleKey, team, role, "insufficient"));
        if (dependencyDelay || toolingFailure || role.ReworkRatePercent >= 20m)
            return new("ImprovementCommitment", roleKey, null,
                dependencyDelay ? "Resolve dependency delay before changing capacity."
                    : toolingFailure ? "Resolve tooling failure before changing capacity."
                    : "Reduce rework and stabilize requirements before changing capacity.",
                Fingerprint(roleKey, team, role, "improvement"));

        var previousRole = previous?.Principals.Where(x => x.RoleKey == roleKey)
            .OrderByDescending(x => x.CompletedStageCount).FirstOrDefault();
        var sustained = previous?.Team.IsOverCapacity == true && team.IsOverCapacity;
        var milestoneImpact = team.ProjectedSprintCount > 1m && team.RemainingPoints > 0m;
        var backlogPressure = role.PendingDemand > Math.Max(1m, role.ThroughputPerWeek);
        var worseningP85 = previousRole is not null && role.P85CycleTimeHours > previousRole.P85CycleTimeHours * 1.1m;
        var capacityBlocking = role.BlockedCount > 0 && role.BlockedDurationHours >= 8m;
        if (sustained && milestoneImpact && (backlogPressure || worseningP85 || capacityBlocking))
            return Proposal(roleKey,
                "Sustained capacity pressure is forecast to affect a milestone and is corroborated by trusted flow data.",
                "growth", team, role);
        return new("NoChange", roleKey, null,
            "Trusted evidence does not yet justify a capacity change.",
            Fingerprint(roleKey, team, role, "no-change"));
    }

    private static ProducerCapacityAssessment Proposal(
        string roleKey, string rationale, string kind, WorkFlowTeamMetrics team, WorkFlowPrincipalMetrics? role) =>
        new("ProposeCapacity", roleKey, 1, rationale, Fingerprint(roleKey, team, role, kind));

    private static string Fingerprint(
        string roleKey, WorkFlowTeamMetrics team, WorkFlowPrincipalMetrics? role, string kind) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            roleKey, kind, team.CompletedSprintCount, team.ProjectedSprintCount, team.RemainingPoints,
            role?.CompletedStageCount, role?.PendingDemand, role?.P85CycleTimeHours,
            role?.BlockedDurationHours, role?.ReworkRatePercent
        })))).ToLowerInvariant();
}

public sealed record ProducerCapacityAssessment(
    string Disposition,
    string RoleKey,
    int? CapacityDelta,
    string Rationale,
    string EvidenceFingerprint);

public static class ProducerPolicyFingerprint
{
    public static string ForPlanning(Guid workstreamId, long teamRevision, string profileDigest, string handoffDigest) =>
        Digest($"{workstreamId:D}|{teamRevision}|{profileDigest}|{handoffDigest}");

    public static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

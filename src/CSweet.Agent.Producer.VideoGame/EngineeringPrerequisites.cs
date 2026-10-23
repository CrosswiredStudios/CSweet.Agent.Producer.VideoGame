using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    // This metadata is read only from the current, authenticated Technical Director proposal.
    // It permits preparation of bounded evidence-producing work; it never answers a decision.
    internal static IReadOnlyList<string> ReadEngineeringPrerequisites(
        GameTechnicalDeliveryProposalV1 proposal, AgentCoordinationArtifact artifact)
    {
        string[] keys;
        if (artifact.Payload.TryGetProperty("engineeringPrerequisiteKeys", out var declared))
            keys = declared.Deserialize<string[]>() ?? throw new InvalidOperationException("Engineering prerequisite keys are missing.");
        else
        {
            // Compatibility with the deterministic prerequisite emitted by Director 5.0.4.
            // No title, question wording, or model claim of approval participates in this match.
            keys = proposal.DeliveryItems.Any(x => x.ProposalKey == "dependency-pin-phaser")
                ? ["dependency-pin-phaser"] : [];
        }
        if (keys.Length > 16 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidOperationException("Engineering prerequisite keys must be bounded and unique.");
        foreach (var key in keys)
        {
            var task = proposal.DeliveryItems.SingleOrDefault(x => x.ProposalKey == key);
            if (task is null || task.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Task ||
                task.AccountableRoleKey != VideoGameRoleKeys.Engineer ||
                !task.RequiredCapabilityKeys.Contains("work.execution.run") ||
                task.AcceptanceCriteria.Count == 0 || task.DependencyProposalKeys.Count != 0 ||
                !proposal.DeliveryItems.Any(x => x.ProposalKey != key && x.DependencyProposalKeys.Contains(key)))
                throw new InvalidOperationException("A planning prerequisite must be a root engineering task with testable criteria and dependent work.");
        }
        return keys;
    }

    internal static bool CanPrepareWithPlanningDecisions(WorkItem item, ProducerPlanningCycleState cycle) =>
        cycle.OutstandingAuthorityQuestions.Count == 0 ||
        item.TypeKey == VideoGameWorkItemTypeKeys.ResearchSpike ||
        item.TypeKey == VideoGameWorkItemTypeKeys.Task &&
        item.ProposalProvenance is { } provenance &&
        provenance.ArtifactDigest == cycle.TechnicalProposalDigest &&
        cycle.EngineeringPrerequisiteKeys.Contains(provenance.ProposalItemKey, StringComparer.Ordinal) &&
        item.Planning?.DelegationRecommendations.Any(x => x.StageKey == "specialist-execution" &&
            x.RequiredRoleKey == VideoGameRoleKeys.Engineer && x.RequiredCapabilityKeys.Contains("work.execution.run")) == true;
}

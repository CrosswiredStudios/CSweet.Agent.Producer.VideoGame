using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string OwnershipContractRevision = "meaningful-ownership.v2";
    internal sealed record PlanningContractReceipt(string Revision, DateTimeOffset ChangedAt);

    internal static async Task<DateTimeOffset> PlanningContractChangedAtAsync(Guid workstream, string fingerprint,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var key = AgentOperatingStateKey.Create("producer-planning-contract:v1", new { workstream, fingerprint });
        var current = await context.Platform.ReadOperatingStateAsync<PlanningContractReceipt>(key, ct);
        if (current?.Payload.Revision == OwnershipContractRevision) return current.Payload.ChangedAt;
        var changed = DateTimeOffset.UtcNow;
        var recorded = await new RevisionSafeProjectState(context.Platform).MergeAsync<PlanningContractReceipt>(key,
            "planning.contract.v1", 1, previous => previous?.Revision == OwnershipContractRevision ? previous :
                new(OwnershipContractRevision, changed), new Dictionary<string, string> { ["contract"] = OwnershipContractRevision },
            AgentOperatingStateKey.Create("planning-contract-write:v1", new { key, OwnershipContractRevision }), ct);
        return recorded.Payload.ChangedAt;
    }

    internal static PlanningFinding OwnershipFinding(GameProductionPlanningCycleV1 cycle, string outlineDigest,
        BoundOwnershipGap gap, IReadOnlyList<GameProposedWorkItemV1> items, string actor) =>
        new(AgentOperatingStateKey.Create("planning-reconciliation:v1", new { cycle.WorkstreamId, cycle.ApprovedPackageDigest,
                outlineDigest, gap.Statement, gap.AffectedProposalKeys }), gap.Statement, gap.Reason, gap.AffectedProposalKeys,
            items.Where(i => gap.AffectedProposalKeys.Contains(i.ProposalKey)).Select(i => i.Title).ToArray(), actor,
            gap.ConflictEvidence.Count > 0 ? "Review the incompatible requirements within delegated management authority" :
                "Reconcile the approved obligation with the existing plan or name the missing planning responsibility");

    internal static async Task<IReadOnlyList<PlanningOwnershipEvidence>> ReadOwnershipContributionsAsync(
        ProducerPlanningCycleState state, ProgressiveEvidence roadmap, ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken ct)
    {
        var evidence = new List<PlanningOwnershipEvidence>();
        foreach (var pending in state.PlanningReconciliations.Where(p => p.OutlineDigest == roadmap.OutlineDigest))
        {
            var session = await context.Platform.Communication.ReadCoordinationAsync(pending.SessionId, ct);
            if (session.BoardSource?.BoardId != state.BoardId || session.Initiator.OrganizationUserId.ToString() != context.Identity?.EmployeeId ||
                session.Target.OrganizationUserId != pending.SpecialistId || session.Status != AgentCoordinationStatuses.Completed) continue;
            var request = session.Turns.SingleOrDefault(t => t.SpeakerOrganizationUserId == session.Initiator.OrganizationUserId &&
                t.Artifact?.Type == WorkContributions.RequestType && t.Artifact.Key == pending.Key)?.Artifact;
            var reply = session.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == pending.SpecialistId &&
                t.Artifact?.Type == WorkContributions.AnswerType && t.Artifact.Key == pending.Key)?.Artifact;
            if (request is null || reply is null || request.Payload.Deserialize<WorkContributionRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is not { } input ||
                input.WorkstreamId != state.WorkstreamId || input.BoardId != state.BoardId || input.ScopeKey != pending.Key ||
                input.Sources is not { Count: 1 } || input.Sources[0] != new WorkContributionSource(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)) continue;
            var answer = reply.Payload.Deserialize<WorkContributionAnswer>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.IsNullOrWhiteSpace(answer?.ProposedChange))
                evidence.Add(new(reply.Digest, "Responsibility", answer.ProposedChange, pending.ScopeKeys));
        }
        return evidence;
    }

    internal static async Task<ProducerPlanningCycleState> ReconcileOwnershipFindingsAsync(
        GameProductionPlanningCycleV1 cycle, ProducerPlanningCycleState state, ProgressiveEvidence roadmap,
        OwnershipReview ownership, GameTechnicalDeliveryProposalV1 proposal, AgentTeammate specialist,
        ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken ct)
    {
        var references = state.PlanningReconciliations.ToList();
        var manager = context.Identity?.ManagerDisplayName ?? "the accountable project manager";
        foreach (var gap in ownership.Gaps)
        {
            var finding = OwnershipFinding(cycle, roadmap.OutlineDigest, gap, proposal.DeliveryItems,
                gap.ConflictEvidence.Count > 0 ? manager : specialist.DisplayName);
            if (gap.ConflictEvidence.Count > 0)
            {
                // Manager-owned review of incompatible requirements, not a claim that a measurement failed.
                await EnsurePlanningDecisionsAsync(cycle.WorkstreamId, cycle.BoardId, cycle.PlanningFingerprint,
                    [finding.StatusSummary + " Supporting requirements: " + string.Join("; ", gap.ConflictEvidence.Select(e => e.Text))], handoff, context, ct);
                continue;
            }
            var saved = references.SingleOrDefault(p => p.Key == finding.Key);
            if (saved is not null)
            {
                var completed = await context.Platform.Communication.ReadCoordinationAsync(saved.SessionId, ct);
                if (completed.Status == AgentCoordinationStatuses.Completed)
                {
                    var advice = await ReadOwnershipContributionsAsync(state with { PlanningReconciliations = [saved] }, roadmap, handoff, context, ct);
                    await EnsurePlanningDecisionsAsync(cycle.WorkstreamId, cycle.BoardId, cycle.PlanningFingerprint,
                        [(finding with { AccountableActor = manager, NextAction = "Review the remaining planning responsibility within delegated management authority" }).StatusSummary + " The consultation completed but this responsibility remains unresolved. " +
                         gap.Reason + (advice.Count > 0 ? " Source-bound specialist advice: " + string.Join("; ", advice.Select(e => e.Text)) :
                             " The completed consultation has no usable source-bound reply; obtain the missing evidence before deciding.") +
                         " Review the missing responsibility within delegated management authority; keep only affected work held."], handoff, context, ct);
                }
                continue; // Replays reuse this commission and the same manager question.
            }
            var question = finding.StatusSummary + "\n" + gap.Reason +
                "\nResolve this within the accepted scope. Identify an existing outcome, constraint or approved decision the review missed, " +
                "or propose a specific missing planning responsibility. Do not rewrite unrelated Epics or weaken criteria. " +
                "A hypothetical escalation condition is not an observed failure. State evidence still needed. Advice grants no new authority.";
            var contribution = new WorkContributionRequest(cycle.WorkstreamId, cycle.BoardId, finding.Key, question,
                [new(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)]);
            var artifact = CollaborationActions.WithDocuments(new(WorkContributions.RequestType, "1.0", finding.Key, 1, true,
                JsonSerializer.SerializeToElement(contribution)), [new(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)]);
            var session = await context.Platform.Communication.StartBoardCoordinationAsync(new(Guid.Parse(specialist.EmployeeId), cycle.BoardId,
                "Reconcile planning responsibility", question, ["Identify a meaningful owner without changing approved scope or claiming unobserved evidence."],
                question, finding.Key, artifact), ct);
            await TrackedCollaboration.QueueCardAsync(context, session, new("Receive planning reconciliation", finding.StatusSummary +
                "\n\nDetails: " + gap.Reason)
                { BoardId = cycle.BoardId, TeamId = cycle.TeamId, WorkstreamId = cycle.WorkstreamId }, ct);
            references.Add(new(finding.Key, session.Id, Guid.Parse(specialist.EmployeeId), roadmap.OutlineDigest, finding.AffectedScopeKeys));
        }
        var activeKeys = ownership.Gaps.Select(g => OwnershipFinding(cycle, roadmap.OutlineDigest, g, proposal.DeliveryItems, specialist.DisplayName).Key).ToHashSet();
        return state with { PlanningReconciliations = references.Select(r => r with { Active = activeKeys.Contains(r.Key) }).ToArray(),
            OwnershipHeldProposalKeys = OwnershipHolds(proposal.DeliveryItems, ownership).ToArray() };
    }
}

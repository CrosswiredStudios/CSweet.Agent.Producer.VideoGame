using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string OwnershipContractRevision = "scoped-planning-recovery.v9";
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
        var directory = await context.Platform.Issues.ReadAllAsync(state.BoardId, true, ct);
        var references = state.PlanningReconciliations.Concat(directory.Commissions.Where(c => c.SessionId.HasValue)
            .Select(c => new PlanningReconciliationReference("issue-commission:" + c.Id.ToString("N"), c.SessionId!.Value, c.TargetEmployeeId, roadmap.OutlineDigest,
                directory.Issues.Where(i => c.IssueIds.Contains(i.Id)).SelectMany(i => i.ScopeKeys).Distinct().ToArray()))).DistinctBy(r => r.SessionId).ToArray();
        foreach (var pending in references)
        {
            var session = await context.Platform.Communication.ReadCoordinationAsync(pending.SessionId, ct);
            if (session.BoardSource?.BoardId != state.BoardId || session.Initiator.OrganizationUserId.ToString() != context.Identity?.EmployeeId ||
                session.Target.OrganizationUserId != pending.SpecialistId || pending.OutlineDigest != roadmap.OutlineDigest ||
                session.Status is not (AgentCoordinationStatuses.Completed or "Summarizing")) continue;
            var reply = await WorkContributions.ReadAnswerAsync(session.Turns, session.Initiator.OrganizationUserId,
                pending.SpecialistId, state.WorkstreamId, state.BoardId, context.Platform, ct);
            if (reply is null || reply.Key != pending.Key || reply.Request.Sources is not { Count: 1 } ||
                reply.Request.Sources[0] != new WorkContributionSource(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest)) continue;
            if (!string.IsNullOrWhiteSpace(reply.Answer.ProposedChange))
                evidence.Add(new(reply.Digest, "Responsibility", reply.Answer.ProposedChange, pending.ScopeKeys));
        }
        return evidence;
    }

    // A recorded resolution against the same approved source stands until new recorded evidence exists.
    // A re-run or reworded model judgment is not such evidence, and the platform refuses to reopen on it.
    // The obligation is carried as owned so it neither holds scope nor requests an impossible reopening.
    internal static OwnershipReview CarryRecordedResolutions(OwnershipReview ownership,
        IReadOnlyList<WorkIssueCaseResponse> issues, WorkApprovedSource source)
    {
        var settled = ownership.Gaps.Where(g => g.ObligationKey is { } key &&
            issues.Where(i => i.ObligationKey == key).OrderByDescending(i => i.Episode).FirstOrDefault() is { Status: not "Open" } latest &&
            latest.Source == source).ToArray();
        if (settled.Length == 0) return ownership;
        return ownership with { Gaps = ownership.Gaps.Except(settled).ToArray(),
            Commitments = [.. ownership.Commitments, .. settled.Select(g =>
                new OwnedCommitment(g.Statement, g.Citations, []) { ObligationKey = g.ObligationKey })] };
    }

    internal static async Task<OwnershipReview> CarryRecordedResolutionsAsync(OwnershipReview ownership, Guid boardId,
        ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken ct)
    {
        var directory = await context.Platform.Issues.ReadAllAsync(boardId, true, ct);
        var carried = CarryRecordedResolutions(ownership, directory.Issues,
            new WorkApprovedSource(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest));
        if (carried.Gaps.Count != ownership.Gaps.Count)
            await context.ReportProgressAsync(new { ownershipReview = "recorded resolutions retained", boardId,
                obligations = ownership.Gaps.Except(carried.Gaps).Select(g => g.Statement).ToArray() }, ct);
        return carried;
    }

    internal static async Task<ProducerPlanningCycleState> ReconcileOwnershipFindingsAsync(
        GameProductionPlanningCycleV1 cycle, ProducerPlanningCycleState state, ProgressiveEvidence roadmap,
        OwnershipReview ownership, GameTechnicalDeliveryProposalV1 proposal, AgentTeammate specialist,
        ProducerAcceptedHandoff handoff, AgentRuntimeContext context, CancellationToken ct, AgentLlmSelection? resolutionModel = null)
    {
        var directory = await context.Platform.Issues.ReadAllAsync(cycle.BoardId, true, ct);
        var source = new WorkApprovedSource(handoff.ArtifactId, handoff.AcceptedRevisionId, handoff.RevisionDigest);
        ownership = CarryRecordedResolutions(ownership, directory.Issues, source);
        var decisions = directory.Issues.Any(i => i.Links.Any(l => l.Kind == "Decision"))
            ? await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(WorkstreamId: cycle.WorkstreamId), ct) : [];
        bool AwaitingDecision(WorkIssueCaseResponse issue) => issue.Links.Where(l => l.Kind == "Decision").Any(l => {
            var seen = new HashSet<Guid>(); var id = l.Id;
            while (seen.Add(id)) {
                var decision = decisions.SingleOrDefault(d => d.Id == id);
                if (decision is { Status: DecisionStatuses.Superseded, SupersededByDecisionId: Guid successor }) { id = successor; continue; }
                return decision is not { Status: DecisionStatuses.Decided or "Cancelled" };
            }
            return true;
        });
        // Recovery also covers legacy consultations for responsibilities already owned by
        // the current grounded review. No new consultation is needed merely to retire its tracking.
        var retiredLegacy = new HashSet<Guid>();
        var ownedScopes = ownership.Commitments.SelectMany(c => c.Owners).SelectMany(o => o.ScopeKeys).ToHashSet();
        foreach (var legacy in state.PlanningReconciliations.Where(r => r.Active && r.ScopeKeys.Count > 0 && r.ScopeKeys.All(ownedScopes.Contains) &&
            !ownership.Gaps.SelectMany(g => g.AffectedProposalKeys).Intersect(r.ScopeKeys).Any()))
        {
            var session = await context.Platform.Communication.ReadCoordinationAsync(legacy.SessionId, ct);
            if (session.BoardSource?.BoardId != cycle.BoardId || session.Initiator.OrganizationUserId.ToString() != context.Identity?.EmployeeId ||
                session.Target.OrganizationUserId != legacy.SpecialistId) continue;
            var original = session.Turns.FirstOrDefault(t => t.SpeakerOrganizationUserId == session.Initiator.OrganizationUserId &&
                t.Artifact?.Type == WorkContributions.RequestType && t.Artifact.Key == legacy.Key)?.Artifact;
            var input = original?.Payload.Deserialize<WorkContributionRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (input?.WorkstreamId != cycle.WorkstreamId || input.BoardId != cycle.BoardId || input.Sources is not { Count: 1 } ||
                input.Sources[0] != new WorkContributionSource(source.DocumentId, source.RevisionId, source.Digest)) continue;
            var key = AgentOperatingStateKey.Create("legacy-responsibility:v1", new { session.Id });
            var prior = directory.Issues.SingleOrDefault(i => i.ObligationKey == key);
            if (prior is not { Status: "Resolved" })
            {
                var update = new WorkIssueUpdate(key, "Reconcile the retained source-bound planning responsibility", input.Question, legacy.ScopeKeys) {
                    IssueId = prior?.Id, ExpectedRevision = prior?.Revision, Status = "Resolved", Phase = "AwaitingReview",
                    BriefTitle = "Planning responsibility reconciled", BriefSummary = "The current grounded review names meaningful owners for the affected work.",
                    NextAction = "Continue eligible work; retain earlier advice and failed attempts as history", Links = [new("Coordination", session.Id)],
                    ResolutionEvidence = JsonSerializer.Serialize(new { source, roadmap.OutlineDigest, reviewedOwnership = ownership.Commitments, legacy.ScopeKeys })
                };
                await context.Platform.Issues.ReconcileAsync(new(cycle.BoardId, source, [update],
                    AgentOperatingStateKey.Create("legacy-responsibility-receipt:v1", new { session.Id, source, update })), ct);
            }
            retiredLegacy.Add(legacy.SessionId);
        }
        var updates = new List<WorkIssueUpdate>();
        WorkIssueCaseResponse? Current(string key) => directory.Issues.Where(i => i.ObligationKey == key).OrderByDescending(i => i.Episode).FirstOrDefault();
        var selected = ownership.Commitments.Select(c => c.ObligationKey).Concat(ownership.Gaps.Select(g => g.ObligationKey)).ToHashSet();
        foreach (var obsolete in directory.Issues.Where(i => i.Status == "Open" && i.Source != source && !selected.Contains(i.ObligationKey)))
        {
            if (AwaitingDecision(obsolete)) continue;
            updates.Add(new(obsolete.ObligationKey, obsolete.Statement, obsolete.Detail, obsolete.ScopeKeys) {
                IssueId = obsolete.Id, ExpectedRevision = obsolete.Revision, Status = "Superseded", Phase = "AwaitingReview",
                NextAction = "The accepted replacement basis retires this responsibility; the original evidence remains in history",
                ResolutionEvidence = JsonSerializer.Serialize(new { prior = obsolete.Source, replacement = source, reviewedObligations = selected })
            });
        }
        foreach (var commitment in ownership.Commitments)
        {
            if (commitment.ObligationKey is not { } key || Current(key) is not { Status: "Open" } existing) continue;
            if (AwaitingDecision(existing)) continue;
            updates.Add(new(key, existing.Statement, existing.Detail, existing.ScopeKeys) {
                IssueId = existing.Id, ExpectedRevision = existing.Revision, Status = "Resolved", Phase = "AwaitingReview",
                NextAction = "The planning responsibility is owned; normal execution and acceptance still apply",
                ResolutionEvidence = JsonSerializer.Serialize(new { source, reviewedOwnership = commitment.Owners })
            });
        }
        foreach (var gap in ownership.Gaps)
        {
            var key = gap.ObligationKey ?? throw new InvalidOperationException("Reconcile a grounded obligation identity before commissioning advice.");
            var existing = Current(key);
            var phase = gap.ConflictEvidence.Count > 0 || existing is not null && AwaitingDecision(existing) ? "AwaitingDecision" :
                directory.Commissions.Any(c => c.Status == "AdviceReceived" && existing is not null && c.IssueIds.Contains(existing.Id)) ? "AwaitingReview" : "Working";
            var update = new WorkIssueUpdate(key, existing?.Statement ?? gap.Statement, gap.Reason, gap.AffectedProposalKeys) {
                IssueId = existing?.Id, ExpectedRevision = existing?.Revision, Phase = phase,
                BriefTitle = gap.BriefTitle, BriefSummary = gap.BriefSummary,
                NextAction = gap.Impact == "FutureWork" ? "Own and refine the later outcome; continue independent current work" :
                    phase == "AwaitingDecision" ? "Obtain delegated management direction on the incompatible requirements" :
                    phase == "AwaitingReview" ? "Apply source-bound advice and reassess meaningful ownership" : "Reconcile responsibility within the accepted scope"
            };
            // Presentation rewording is not new substantive work.
            if (existing is not null && existing.Status == "Open" && existing.Phase == update.Phase && existing.Source == source &&
                existing.NextAction == update.NextAction && existing.ScopeKeys.Order().SequenceEqual(update.ScopeKeys.Order())) continue;
            updates.Add(update);
        }
        if (updates.Count > 0)
            await context.Platform.Issues.ReconcileAsync(new(cycle.BoardId, source, updates,
                AgentOperatingStateKey.Create("issue-reconcile:v1", new { cycle.BoardId, source, updates })), ct);
        directory = await context.Platform.Issues.ReadAllAsync(cycle.BoardId, true, ct);
        var active = directory.Issues.Where(i => i.Status == "Open" && i.Source == source).ToArray();
        var pending = new List<WorkIssueCaseResponse>();
        var manager = context.Identity?.ManagerDisplayName ?? "the accountable project manager";
        foreach (var gap in ownership.Gaps)
        {
            // Follow-up work derives from the current issue record, never from the model's gap list alone.
            if (active.SingleOrDefault(i => i.ObligationKey == gap.ObligationKey) is not { } issue) continue;
            var finding = OwnershipFinding(cycle, roadmap.OutlineDigest, gap, proposal.DeliveryItems, specialist.DisplayName);
            var commissions = directory.Commissions.Where(c => c.IssueIds.Contains(issue.Id) && c.Source == source).ToArray();
            async Task AskManagerAsync(string question)
            {
                var currentDecisions = await EnsurePlanningDecisionsAsync(cycle.WorkstreamId, cycle.BoardId, cycle.PlanningFingerprint, [question], handoff, context, ct, resolutionModel);
                var decision = currentDecisions.LastOrDefault(d => d.Summary == question && d.Status != DecisionStatuses.Superseded);
                if (decision is null || issue.Links.Any(l => l.Kind == "Decision" && l.Id == decision.Id)) return;
                var link = new WorkIssueUpdate(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) {
                    IssueId = issue.Id, ExpectedRevision = issue.Revision, Phase = "AwaitingDecision", NextAction = "Incorporate the accountable manager's recorded direction",
                    Links = [new("Decision", decision.Id)]
                };
                await context.Platform.Issues.ReconcileAsync(new(cycle.BoardId, source, [link], AgentOperatingStateKey.Create("issue-decision:v1", new { issueId = issue.Id, decisionId = decision.Id })), ct);
            }
            if (gap.ConflictEvidence.Count > 0)
                await AskManagerAsync((finding with { AccountableActor = manager }).StatusSummary + " Supporting requirements: " + string.Join("; ", gap.ConflictEvidence.Select(e => e.Text)));
            else if (commissions.Any(c => c.Status is "AdviceReceived" or "NeedsReview" or "NeedsAuthority"))
            {
                var advice = await ReadOwnershipContributionsAsync(state, roadmap, handoff, context, ct);
                await AskManagerAsync("Reconcile the remaining responsibility: " + issue.Statement + ". Affected work: " +
                    string.Join(", ", proposal.DeliveryItems.Where(i => issue.ScopeKeys.Contains(i.ProposalKey)).Select(i => i.Title)) +
                    ". Accountable: " + manager + ". No user decision is requested. Review retained specialist advice within delegated authority: " +
                    string.Join("; ", advice.Where(a => a.ScopeKeys.Intersect(issue.ScopeKeys).Any() ||
                        issue.ScopeKeys.Count == 0 && a.ScopeKeys.Count == 0).Select(a => a.Text)));
            }
            else if (!commissions.Any(c => c.Status is "Pending" or "Dispatched")) pending.Add(issue);
        }
        var transferred = retiredLegacy;
        if (pending.Count > 0)
        {
            var question = "Reconcile these related planning responsibilities against the accepted source. Identify meaningful existing owners or propose missing responsibility. " +
                "Preserve product criteria and evidence requirements. Hypothetical failures are not observations. Advice grants no changed authority.\n" +
                string.Join("\n", pending.Select(i => i.Statement + " — " + i.Detail));
            var legacy = state.PlanningReconciliations.Where(r => r.Active && r.SpecialistId == Guid.Parse(specialist.EmployeeId)).Select(r => r.SessionId).Distinct().ToArray();
            await context.Platform.Issues.CommissionAsync(new(cycle.BoardId, Guid.Parse(specialist.EmployeeId), pending.Select(i => i.Id).Order().ToArray(), source, question,
                AgentOperatingStateKey.Create("issue-commission-request:v1", new { cycle.BoardId, source, target = specialist.EmployeeId, members = pending.Select(i => i.Id).Order().ToArray() })) { SupersededSessionIds = legacy }, ct);
            transferred.UnionWith(legacy);
        }
        directory = await context.Platform.Issues.ReadAllAsync(cycle.BoardId, true, ct);
        var heldRoots = directory.Issues.Where(i => i.Status == "Open" && i.Source == source).SelectMany(i => i.ScopeKeys).Distinct().ToArray();
        var known = proposal.DeliveryItems.Select(i => i.ProposalKey).ToHashSet(StringComparer.Ordinal);
        var held = PlanningScopeOwnership.BlockedScopes(proposal.DeliveryItems.Select(i => new PlanningScopeDependency(i.ProposalKey, i.DependencyProposalKeys, i.ParentProposalKey)).ToArray(),
            heldRoots.Where(known.Contains));
        return state with { PlanningReconciliations = state.PlanningReconciliations.Select(r => transferred.Contains(r.SessionId) ? r with { Active = false } : r).ToArray(),
            OwnershipHeldProposalKeys = held.ToArray() };
    }
}

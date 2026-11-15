using System.Text.Json;
using System.Text.Json.Serialization;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ReviewedCommitment(string Statement, IReadOnlyList<int> Sources, IReadOnlyList<int> EvidenceIndexes)
    {
        public IReadOnlyList<int> ProposedIndexes { get; init; } = [];
    }
    internal sealed record OwnershipGap(string Statement, int SourceIndex, string Reason)
    {
        public IReadOnlyList<int> ProposedIndexes { get; init; } = [];
        public IReadOnlyList<int> AffectedEpicIndexes { get; init; } = [];
        public IReadOnlyList<int> ConflictEvidenceIndexes { get; init; } = [];
    }
    internal sealed record OwnershipDraft(IReadOnlyList<ReviewedCommitment> Commitments, IReadOnlyList<OwnershipGap> Gaps);
    internal sealed record BoundOwner(string Epic, string Criterion)
    {
        public PlanningOwnershipEvidence? Evidence { get; init; }
        [JsonIgnore] public IReadOnlyList<string> ScopeKeys => Evidence?.ScopeKeys ?? [Epic];
    }
    internal sealed record OwnedCommitment(string Statement, IReadOnlyList<string> Citations, IReadOnlyList<BoundOwner> Owners);
    internal sealed record BoundOwnershipGap(string Statement, string Reason, IReadOnlyList<string> AffectedProposalKeys)
    {
        public IReadOnlyList<PlanningOwnershipEvidence> ConflictEvidence { get; init; } = [];
    }
    internal sealed record OwnershipReview(IReadOnlyList<OwnedCommitment> Commitments, IReadOnlyList<BoundOwnershipGap> Gaps)
    {
        [JsonIgnore] public IReadOnlyList<ProgressiveCoverage> Coverage => Commitments.Select(c => new ProgressiveCoverage(
            c.Statement, c.Owners.SelectMany(o => o.ScopeKeys).Distinct().ToArray()))
            .Concat(Gaps.Select(g => new ProgressiveCoverage(g.Statement, []))).ToArray();
    }

    private static string OwnershipKey(GameProductionPlanningCycleV1 cycle, ProgressiveEvidence roadmap) =>
        AgentOperatingStateKey.Create("producer-roadmap-ownership.v2", new { cycle.WorkstreamId, cycle.ApprovedPackageDigest,
            roadmap.ScopeKey, roadmap.OutlineDigest, directions = roadmap.ReviewedDirectionsDigest ?? roadmap.DirectionsDigest });

    internal static IReadOnlyList<PlanningOwnershipEvidence> OwnershipCatalog(IReadOnlyList<GameProposedWorkItemV1> epics,
        IReadOnlyList<string> constraints, IReadOnlyList<string> directions)
    {
        var all = epics.Select(e => e.ProposalKey).ToArray();
        var result = new List<PlanningOwnershipEvidence>();
        void Add(string kind, string text, IReadOnlyList<string> scope)
        {
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new(AgentOperatingStateKey.Create("planning-evidence:v1", new { kind, text, scope }), kind, text, scope));
        }
        foreach (var epic in epics)
        {
            Add("Outcome", epic.Title + ": " + epic.Description, [epic.ProposalKey]);
            foreach (var criterion in epic.AcceptanceCriteria) Add("AcceptanceCriterion", criterion, [epic.ProposalKey]);
        }
        foreach (var constraint in constraints) Add("Constraint", constraint, all);
        foreach (var direction in directions) Add("Decision", direction, all);
        return result;
    }

    private async Task<OwnershipReview> ReviewOwnershipAsync(GameProductionPlanningCycleV1 cycle, GameTechnicalDeliveryProposalV1 proposal,
        ProgressiveEvidence roadmap, IReadOnlyList<GameOutcomeSource> sources, IReadOnlyList<string> directions,
        AgentRuntimeContext context, CancellationToken token, IReadOnlyList<PlanningOwnershipEvidence>? contributions = null)
    {
        ValidateProgressiveProposal(proposal, roadmap with { Phase = "Outline", DetailedStoryKeys = [] },
            proposal, roadmap with { Phase = "Outline", DetailedStoryKeys = [] }, [], null);
        var epics = proposal.DeliveryItems.Where(e => e.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone).ToArray();
        var catalog = OwnershipCatalog(epics, proposal.TechnicalConstraints, directions).Concat(contributions ?? []).ToArray();
        var sourceIndexes = sources.Select((s, index) => (s.Key, index)).ToDictionary(x => x.Key, x => x.index);
        var proposed = roadmap.ScopeCoverage.Where(c => c.Statement != null).Select((c, index) => new {
            index, c.Statement, sources = (c.Citations ?? []).Select(k => sourceIndexes.TryGetValue(k, out var i) ? i :
                throw new InvalidOperationException("Roadmap commitment cites a source outside the accepted package.")).ToArray(), c.ProposalKeys }).ToArray();
        // Contributions change the review basis; old criterion-only reviews cannot be replayed.
        var key = AgentOperatingStateKey.Create("producer-roadmap-review.v2", new { projection = OwnershipKey(cycle, roadmap), catalog });
        var reviewed = await PitchProtocol.CachedAsync(key, context, async () => {
            using var client = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                throw new InvalidOperationException("Configure the Producer planning provider."), Settings.GetString("llmModel"), new(InvocationKind: "producer-roadmap-ownership")));
            var draft = await AgentModelJson.ReadAsync(client, [new(ChatRole.System, """
                Compare approved sources with the supplied ownership evidence. Identify distinct approved outcomes and binding constraints.
                Consolidate duplicates; skip incidental prose. Preserve quantities, restrictions and evidence requirements.
                An obligation may be meaningfully owned by an Epic outcome, an acceptance criterion, a technical constraint,
                an approved decision, or a reviewed specialist planning contribution. Select supplied evidence indexes;
                do not duplicate constraints and decisions into every Epic criterion. Ownership is planning responsibility, not proof of execution.
                A generic promise to cover everything cannot own a concrete missing behavior. A constraint may enforce a rule
                across work; it does not prove a feature was implemented or tested. Advice cannot grant changed scope or authority.
                Inspect original sources for omissions. Return commitments with statement, sources and evidenceIndexes.
                For genuine gaps return statement, sourceIndex, reason and affectedEpicIndexes; include dependent scope only if directly affected.
                Statements and reasons explain the finding in human terms; keep internal keys and indexes in reference fields.
                An empty affectedEpicIndexes means impact is unknown and must conservatively hold all scope, not silently clear work.
                Include proposedIndexes on commitments and gaps, accounting for each proposed obligation exactly once.
                Routine missing responsibility is agent reconciliation. A conditional instruction to escalate if an experiment fails
                is not evidence that it failed or is impossible. Do not invent observed measurements, participant studies or technical failures.
                conflictEvidenceIndexes is empty for ordinary omissions/hypotheses. Use it only for established incompatible
                binding constraints or decisions that actually require changed authority; cite the exact incompatible evidence.
                Empty gaps means every obligation has a meaningful owner; it never means all work is complete.
                """), new(ChatRole.User, JsonSerializer.Serialize(new { sources = sources.Select((s, index) => new { index, s.ApprovedText }),
                    proposed, epics = epics.Select((e,index) => new { index,e.ProposalKey,e.Title }),
                    evidence = catalog.Select((e,index) => new { index,e.Kind,e.Text,e.ScopeKeys }) }))],
                new AgentModelJsonRequest<OwnershipDraft>("approved scope ownership") { CorrectionLimit = 1, Options = ResponseOptions(),
                    Normalize = value => { BindOwnership(value, sources, epics, proposed.Length, catalog); return value; } }, token);
            return BindOwnership(draft, sources, epics, proposed.Length, catalog);
        }, token);
        // Preserve the current projection for sprint review/reconnect discovery, without treating a cached judgment as authority.
        var state = new RevisionSafeProjectState(context.Platform);
        await state.MergeAsync<OwnershipReview>(OwnershipKey(cycle, roadmap), "game.scope-ownership.v2", 2, _ => reviewed,
            new Dictionary<string,string>(), AgentOperatingStateKey.Create("ownership-projection", new { key, reviewed }), token);
        return reviewed;
    }

    internal static OwnershipReview BindOwnership(OwnershipDraft draft, IReadOnlyList<GameOutcomeSource> sources,
        IReadOnlyList<GameProposedWorkItemV1> epics, int proposedCount = 0, IReadOnlyList<PlanningOwnershipEvidence>? suppliedCatalog = null)
    {
        var catalog = suppliedCatalog ?? OwnershipCatalog(epics, [], []);
        if (draft.Commitments == null || draft.Gaps == null || draft.Commitments.Count + draft.Gaps.Count == 0)
            throw new AgentModelJsonException("Return grounded commitments or specific gaps for the accepted project.");
        var statements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Distinct(string? text) => !string.IsNullOrWhiteSpace(text) && statements.Add(text.Trim());
        bool Source(int i) => i >= 0 && i < sources.Count;
        var commitments = new List<OwnedCommitment>();
        foreach (var c in draft.Commitments)
        {
            if (c == null || !Distinct(c.Statement) || c.Sources is not { Count: > 0 } || !c.Sources.All(Source) || c.EvidenceIndexes is not { Count: > 0 })
                throw new AgentModelJsonException("Give each obligation sources and supplied ownership evidence, or record a gap.");
            IReadOnlyList<PlanningOwnershipEvidence> evidence;
            try { evidence = PlanningScopeOwnership.BindEvidence(c.EvidenceIndexes, catalog); }
            catch (ArgumentException ex) { throw new AgentModelJsonException(ex.Message); }
            commitments.Add(new(c.Statement.Trim(), c.Sources.Distinct().Select(i => sources[i].Key).ToArray(),
                evidence.Select(e => new BoundOwner(e.ScopeKeys.Count == 1 ? e.ScopeKeys[0] : "", e.Text) { Evidence = e }).ToArray()));
        }
        var gaps = new List<BoundOwnershipGap>();
        foreach (var g in draft.Gaps)
        {
            if (g == null || !Distinct(g.Statement) || !Source(g.SourceIndex) || string.IsNullOrWhiteSpace(g.Reason) ||
                g.AffectedEpicIndexes is null || g.AffectedEpicIndexes.Any(i => i < 0 || i >= epics.Count))
                throw new AgentModelJsonException("Give each gap an approved source, reason and valid affected scope indexes.");
            IReadOnlyList<PlanningOwnershipEvidence> conflict = [];
            if (g.ConflictEvidenceIndexes is { Count: > 0 })
            {
                try { conflict = PlanningScopeOwnership.BindEvidence(g.ConflictEvidenceIndexes, catalog); }
                catch (ArgumentException ex) { throw new AgentModelJsonException(ex.Message); }
                // Incompatibility needs distinct binding requirements, not a hypothetical instruction or unverified advice.
                if (conflict.Count < 2 || conflict.Any(e => e.Kind is not ("Constraint" or "Decision")))
                    throw new AgentModelJsonException("An authority conflict must identify the incompatible binding constraints or decisions. Missing evidence is routine reconciliation.");
            }
            gaps.Add(new(g.Statement.Trim(), g.Reason.Trim(), (g.AffectedEpicIndexes.Count == 0 ? epics.Select(e => e.ProposalKey) :
                g.AffectedEpicIndexes.Distinct().Select(i => epics[i].ProposalKey)).ToArray()) { ConflictEvidence = conflict });
        }
        var accounted = draft.Commitments.SelectMany(c => c.ProposedIndexes ?? []).Concat(draft.Gaps.SelectMany(g => g.ProposedIndexes ?? [])).Order().ToArray();
        if (!accounted.SequenceEqual(Enumerable.Range(0, proposedCount))) throw new AgentModelJsonException("Account for every proposed commitment exactly once.");
        return new(commitments, gaps);
    }

    internal static IReadOnlySet<string> OwnershipHolds(IReadOnlyList<GameProposedWorkItemV1> items, OwnershipReview? review) =>
        review is null || review.Gaps.Count == 0 ? new HashSet<string>() :
            PlanningScopeOwnership.BlockedScopes(items.Select(i => new PlanningScopeDependency(i.ProposalKey,i.DependencyProposalKeys,i.ParentProposalKey)).ToArray(),
                review.Gaps.SelectMany(g => g.AffectedProposalKeys));

    internal static IReadOnlyList<GameProposedWorkItemV1> OwnershipCandidates(IReadOnlyList<GameProposedWorkItemV1> candidates,
        IReadOnlyList<GameProposedWorkItemV1> items, OwnershipReview? review)
    {
        var held = OwnershipHolds(items,review);
        return candidates.Where(c => !held.Contains(c.ProposalKey) && (c.ParentProposalKey is null || !held.Contains(c.ParentProposalKey))).ToArray();
    }
}

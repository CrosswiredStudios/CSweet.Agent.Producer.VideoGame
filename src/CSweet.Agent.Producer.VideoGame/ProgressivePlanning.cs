using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ProgressiveRequest(string Phase, string DirectionsDigest, string? ScopeKey = null,
        string? OutlineDigest = null, string? StoryKey = null, IReadOnlyList<string>? DetailedStoryKeys = null);
    internal sealed record ProgressiveCoverage(string SourceKey, IReadOnlyList<string> ProposalKeys);
    internal sealed record ProgressiveEvidence(string Phase, string ScopeKey, string OutlineDigest, string DirectionsDigest,
        IReadOnlyList<string> DetailedStoryKeys, IReadOnlyList<ProgressiveCoverage> ScopeCoverage);
    private sealed record ProgressiveScopePlan(IReadOnlyList<GameProposedWorkItemV1> DeliveryItems,
        IReadOnlyList<string> FeasibilityFindings, IReadOnlyList<string> TechnicalConstraints, IReadOnlyList<string> OpenFeasibilityDecisions)
    {
        // Match the Technical Director's frozen PlanningOutput, including its empty
        // progressive-outline pin ledger. Omitting this field changes an unchanged scope's hash.
        public IReadOnlyList<object> DependencyPins { get; init; } = [];
    }
    internal sealed record ProgressiveAdvance(ProgressivePlanningProgress Progress, AgentCoordinationSession? Session = null,
        string? Waiting = null, string? Blocker = null, bool Complete = false);
    private static readonly JsonSerializerOptions ProgressiveJson = new(JsonSerializerDefaults.Web);

    internal static ProgressiveEvidence? ReadProgressiveEvidence(AgentCoordinationArtifact artifact) =>
        artifact.Payload.TryGetProperty("progressivePlanning", out var evidence) ? evidence.Deserialize<ProgressiveEvidence>(ProgressiveJson) : null;

    internal static AgentCoordinationArtifactSubmission ProgressiveRequestArtifact(GameProductionPlanningCycleV1 cycle,
        IReadOnlyList<ArtifactPackageMemberDigest> members, ProgressiveRequest request)
    {
        var artifact = PlanningArtifact(cycle, members);
        var payload = JsonNode.Parse(artifact.Payload.GetRawText())!.AsObject();
        payload["progressivePlanning"] = JsonSerializer.SerializeToNode(request, ProgressiveJson);
        return artifact with { Payload = JsonSerializer.SerializeToElement(payload) };
    }

    internal static bool IsScopeContainer(GameProposedWorkItemV1 item) => item.WorkItemTypeKey is
        VideoGameWorkItemTypeKeys.Milestone or VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content;

    internal static string ProgressiveScopeKey(GameProductionPlanningCycleV1 cycle, string directionsDigest) =>
        "technical-progressive:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            cycle.WorkstreamId, cycle.TeamId, cycle.BoardId, cycle.ProfileDigest, cycle.ApprovedPackageId,
            cycle.ApprovedPackageVersion, cycle.ApprovedPackageDigest, cycle.PlanningFingerprint, directionsDigest })));

    internal static void ValidateProgressiveProposal(GameTechnicalDeliveryProposalV1 outline, ProgressiveEvidence original,
        GameTechnicalDeliveryProposalV1 proposal, ProgressiveEvidence current, IReadOnlyList<string> priorStories, string? requestedStory)
    {
        if (original.Phase != "Outline" || original.DetailedStoryKeys.Count != 0 || original.OutlineDigest.Length != 64 ||
            outline.DeliveryItems.Any(x => !IsScopeContainer(x)) ||
            original.ScopeKey != ProgressiveScopeKey(outline.Cycle, original.DirectionsDigest))
            throw new InvalidOperationException("Progressive planning requires an exact accepted full-scope outline, without executable leaves.");
        var canonical = new { Plan = new ProgressiveScopePlan(outline.DeliveryItems, outline.FeasibilityFindings,
            outline.TechnicalConstraints, outline.OpenFeasibilityDecisions), Coverage = original.ScopeCoverage };
        if (Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))) != original.OutlineDigest)
            throw new InvalidOperationException("The delivered outline does not match its retained scope digest.");
        if (proposal.Cycle.PlanningFingerprint != outline.Cycle.PlanningFingerprint || proposal.Cycle.ApprovedPackageDigest != outline.Cycle.ApprovedPackageDigest ||
            current.ScopeKey != original.ScopeKey || current.OutlineDigest != original.OutlineDigest || current.DirectionsDigest != original.DirectionsDigest)
            throw new InvalidOperationException("The increment belongs to different accepted inputs or scope.");
        var containers = proposal.DeliveryItems.Where(IsScopeContainer).ToArray();
        if (JsonSerializer.Serialize(containers) != JsonSerializer.Serialize(outline.DeliveryItems))
            throw new InvalidOperationException("An increment cannot rename, remove or rewrite the accepted full-scope outline.");
        var expected = priorStories.Concat(requestedStory is null ? [] : new[] { requestedStory }).ToHashSet();
        if (current.DetailedStoryKeys.Count != expected.Count || !current.DetailedStoryKeys.ToHashSet().SetEquals(expected))
            throw new InvalidOperationException("The increment lost prior detail or introduced unrequested future Stories.");
        var leaves = proposal.DeliveryItems.Where(x => !IsScopeContainer(x)).ToArray();
        if (leaves.Select(x => x.ProposalKey).Distinct().Count() != leaves.Length || leaves.Any(x => x.ParentProposalKey is null || !expected.Contains(x.ParentProposalKey)))
            throw new InvalidOperationException("Only validated detailed Stories may publish executable tasks.");
        foreach (var story in outline.DeliveryItems.Where(x => expected.Contains(x.ProposalKey)))
        {
            var tasks = leaves.Where(x => x.ParentProposalKey == story.ProposalKey).ToArray();
            if (tasks.Length == 0 || story.AcceptanceCriteria.Any(criterion => !tasks.Any(task => task.AcceptanceCriteria.Contains(criterion, StringComparer.Ordinal))))
                throw new InvalidOperationException("Every detailed Story must retain its original acceptance criteria in execution/QA work.");
        }
        var known = leaves.Select(x => x.ProposalKey).ToHashSet();
        if (leaves.Any(x => x.DependencyProposalKeys.Any(key => !known.Contains(key))))
            throw new InvalidOperationException("An increment depends on unplanned future executable work.");
    }

    internal static string? NextProgressiveStory(IReadOnlyList<GameProposedWorkItemV1> outline,
        IReadOnlySet<string> detailed, IReadOnlySet<string> completed)
    {
        var byKey = outline.ToDictionary(x => x.ProposalKey);
        bool Satisfied(string key) => completed.Contains(key) || byKey.TryGetValue(key, out var item) &&
            item.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone && outline.Where(x => x.ParentProposalKey == key).Any() &&
            outline.Where(x => x.ParentProposalKey == key).All(x => completed.Contains(x.ProposalKey));
        return outline.FirstOrDefault(x => x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone && !detailed.Contains(x.ProposalKey) &&
            x.DependencyProposalKeys.All(Satisfied) && x.ParentProposalKey is { } parent && byKey[parent].DependencyProposalKeys.All(Satisfied))?.ProposalKey;
    }

    internal static IReadOnlyList<Guid> DetailedIncrementEpicIds(IReadOnlyList<WorkItem> items) => items.Where(epic =>
        epic.Kind == "Epic" && epic.ExecutionMode == WorkItemExecutionModes.Container && epic.Status is not ("Done" or "Completed" or "Cancelled") &&
        items.Any(story => story.ParentItemId == epic.Id && story.Kind == "Story") &&
        items.Where(story => story.ParentItemId == epic.Id).All(story => story.Kind == "Story" &&
            items.Any(task => task.ParentItemId == story.Id && task.ExecutionMode == WorkItemExecutionModes.Executable && task.Status != "Cancelled")))
        .Select(epic => epic.Id).ToArray();

    internal static bool HasAcceptedProgressiveIncrements(IReadOnlyList<GameProposedWorkItemV1> outline,
        IReadOnlyList<string> detailed, IReadOnlyList<WorkItem> items, IReadOnlyList<WorkDeliveryPlanResponse> plans)
    {
        var accepted = plans.Where(plan => plan.Status == "Completed").SelectMany(plan => plan.EpicItemIds).ToHashSet();
        return detailed.All(key => outline.SingleOrDefault(story => story.ProposalKey == key)?.ParentProposalKey is { } epic &&
            items.SingleOrDefault(item => ProposalKey(item) == epic) is { } published && accepted.Contains(published.Id));
    }

    internal static async Task<ProgressiveAdvance> AdvanceProgressivePlanningAsync(Guid boardId, AgentTeamContext roster,
        GameProductionPlanningCycleV1 cycle, IReadOnlyList<ArtifactPackageMemberDigest> members, AgentTeammate technicalDirector,
        AgentCoordinationSession outlineSession, AgentCoordinationArtifact outlineArtifact, GameTechnicalDeliveryProposalV1 outline,
        ProgressivePlanningProgress? progress, AgentRuntimeContext context, CancellationToken token)
    {
        var evidence = ReadProgressiveEvidence(outlineArtifact)!;
        progress ??= new(evidence.ScopeKey, evidence.OutlineDigest, evidence.DirectionsDigest, []);
        try
        {
            ValidateProgressiveProposal(outline, evidence, outline, evidence, [], null);
            if (progress.ScopeKey != evidence.ScopeKey || progress.OutlineDigest != evidence.OutlineDigest)
                throw new InvalidOperationException("Progressive state refers to different approved scope; reconcile the amendment before proceeding.");
            AgentCoordinationSession? currentSession = null;
            AgentCoordinationArtifact? currentArtifact = null;
            GameTechnicalDeliveryProposalV1 current = outline;
            if (progress.RefinementSessionId is { } sessionId)
            {
                currentSession = await context.Platform.Communication.ReadCoordinationAsync(sessionId, token);
                if (currentSession.Target.OrganizationUserId != outlineSession.Target.OrganizationUserId ||
                    currentSession.Initiator.OrganizationUserId != outlineSession.Initiator.OrganizationUserId || currentSession.BoardSource?.BoardId != boardId)
                    throw new InvalidOperationException("The refinement session no longer belongs to this board and planning authority.");
                if (currentSession.Status is "Failed" or "Blocked" or "Cancelled")
                    return new(progress, Blocker: PlanningRecoveryMarker + ": The current increment's refinement is blocked. Preserve the published outline and address the specialist diagnostic before retrying: " + currentSession.FinalSummary);
                if (currentSession.Status != "Completed") return new(progress, Waiting: "The complete scope outline is published. The Technical Director is refining only the next increment.");
                currentArtifact = currentSession.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == currentSession.Target.OrganizationUserId &&
                    x.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact
                    ?? throw new InvalidOperationException("The completed refinement has no authoritative technical proposal.");
                current = currentArtifact.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()!;
                var currentEvidence = ReadProgressiveEvidence(currentArtifact) ?? throw new InvalidOperationException("The refinement omitted progressive scope evidence.");
                ValidateProgressiveProposal(outline, evidence, current, currentEvidence, progress.DetailedStoryKeys, progress.PendingStoryKey);
                progress = progress with { DetailedStoryKeys = currentEvidence.DetailedStoryKeys, PendingStoryKey = null };
                if (progress.ScheduledArtifactDigest != currentArtifact.Digest) return new(progress, currentSession);
            }
            else
            {
                var designer = new GameDesignerBacklogProposalV1(cycle, [], ["Preserve every approved scope requirement and non-goal."], [], outline.ProposalDigest);
                await PublishCanonicalBacklogAsync(boardId, roster, cycle, members, outlineSession, outlineArtifact,
                    designer, outlineSession, outlineArtifact, outline, context, token);
            }
            var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
            var published = board.Items.Where(x => ProposalKey(x) is not null).ToDictionary(x => ProposalKey(x)!);
            var leaves = current.DeliveryItems.Where(x => !IsScopeContainer(x)).ToArray();
            if (leaves.Any(x => !published.TryGetValue(x.ProposalKey, out var item) || item.Status is not ("Done" or "Completed")))
                return new(progress, Waiting: "The current increment is published; estimates, QA readiness and execution remain authoritative. Later Stories retain their approved scope without premature detail.");
            if (leaves.Length > 0 && !HasAcceptedProgressiveIncrements(outline.DeliveryItems, progress.DetailedStoryKeys, board.Items,
                await context.Platform.Work.ReadDeliveryPlansAsync(new(cycle.WorkstreamId), token)))
                return new(progress, Waiting: "The current increment awaits independent aggregate delivery acceptance before the next Story can be refined.");
            var completed = progress.DetailedStoryKeys.Where(story => leaves.Any(x => x.ParentProposalKey == story) &&
                leaves.Where(x => x.ParentProposalKey == story).All(x => published.TryGetValue(x.ProposalKey, out var item) && item.Status is "Done" or "Completed")).ToHashSet();
            if (completed.Count == outline.DeliveryItems.Count(x => x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone))
                return new(progress, Complete: true);
            var next = NextProgressiveStory(outline.DeliveryItems, progress.DetailedStoryKeys.ToHashSet(), completed)
                ?? throw new InvalidOperationException("No dependency-ready Story exists. Retained scope was not removed; reconcile its dependency graph.");
            var request = new ProgressiveRequest("Story", evidence.DirectionsDigest, evidence.ScopeKey, evidence.OutlineDigest, next, progress.DetailedStoryKeys);
            var session = await context.Platform.Communication.StartBoardCoordinationAsync(new(
                Guid.Parse(technicalDirector.EmployeeId), boardId, "Refine next playable increment: " + next,
                "Refine only this approved Story into small executable engineering/QA work; preserve the entire published scope outline and completed increments.",
                ["The exact frozen scope outline is preserved.", "Only the requested Story is detailed.", "Every original criterion survives in execution/QA tasks."],
                "Refine Story " + next + " within the existing approved scope. Other Stories stay planned; no new creative approval or completed work is inferred.",
                BoundedMutationKey("progressive-story:" + evidence.ScopeKey + ":" + evidence.OutlineDigest + ":" + next),
                ProgressiveRequestArtifact(cycle, members, request)), token);
            return new(progress with { RefinementSessionId = session.Id, PendingStoryKey = next }, Waiting:
                "The complete scope outline is published. The next dependency-ready Story has a durable refinement request.");
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException)
        {
            return new(progress, Blocker: PlanningRecoveryMarker + ": [csweet.planning.generator] C-Sweet technical issue: " +
                "Progressive planning retained the existing scope but could not advance: " + error.Message +
                " Escalate through the reporting manager for a code correction. Retry only after its cause changes; accepted scope and saved specialist output are retained.");
        }
    }
}

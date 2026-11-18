using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private sealed record StoryAttempt(AgentCoordinationSession Session, AgentCoordinationArtifact Request, ProgressiveRequest Scope);

    internal static async Task<string?> ReconcileRecoveryDirectionAsync(GameProductionPlanningCycleV1 cycle,
        ProducerAcceptedHandoff handoff, StoryReviewDraft draft,
        IReadOnlyList<DecisionRecord> decisions, AgentRuntimeContext context, CancellationToken token)
    {
        var actions = await context.Platform.ReadOperatingStateAsync<ReviewProposals>("game-review-proposals:" + cycle.WorkstreamId.ToString("N"), token);
        foreach (var action in actions?.Payload.Actions ?? [])
        {
            if (action.Kind is not ("escalate" or "ceo-hold") || action.StoryKey != null && action.StoryKey != draft.Story.ProposalKey) continue;
            var prior = DecisionContinuations.Current(action.Id, cycle.WorkstreamId, decisions);
            if (prior is null || prior.Status != DecisionStatuses.Decided)
                return "The recorded escalation still requires a disposition by its decision authority.";
            if (!prior.Evidence.Any(e => e.ResourceId == handoff.ArtifactId && e.RevisionId == handoff.AcceptedRevisionId && e.Digest == handoff.RevisionDigest)) continue;
            if (prior.AuthorityRuleKey != "work-planning" || prior.DecidedByOrganizationUserId is not Guid manager ||
                !Guid.TryParse(context.Identity?.ManagerEmployeeId, out var reportingManager) || reportingManager != manager)
                return $"Decision {prior.Id:D} requires the original authority to reassess the recovered contribution.";
            var basis = new ContributionAdmission(cycle.BoardId, draft.Story.ProposalKey, draft.SessionId, draft.ArtifactDigest,
                prior.Id, prior.Revision, manager, prior.Summary + " " + prior.Rationale,
                draft.Tasks.Select(t => t.Title + " — " + string.Join(" / ", t.AcceptanceCriteria)).ToArray());
            var key = AgentOperatingStateKey.Create("recovered-contribution:v1", new {
                cycle.BoardId, basis.PriorDecisionId, basis.PriorRevision, basis.SessionId, basis.ArtifactDigest });
            var decision = await context.Platform.RequestDecisionAsync(new(cycle.WorkstreamId, prior.TypeKey,
                $"Can the recovered {draft.Story.Title} now be admitted under your recorded direction?", prior.AuthorityRuleKey,
                [new(ContributionAdmission.Admit, "Admit this recovered plan", "The returned Story satisfies the planning hold. Staffing and execution checks still apply."),
                 new(ContributionAdmission.Hold, "Keep this Story on hold", "Name the missing evidence and its owner.")],
                ContributionAdmission.Hold, prior.Evidence, null, "Only this recovered Story awaits its issuing authority.",
                null, key, JsonSerializer.SerializeToElement(basis, JsonSerializerOptions.Web)), token);
            decision = await DecisionContinuations.ReadCurrentAsync(context.Platform, decision.Id, cycle.WorkstreamId, token);
            if (decision is null || ContributionAdmission.Read(decision.TypeData) is not { } current ||
                current.BoardId != cycle.BoardId || current.StoryKey != basis.StoryKey || current.SessionId != basis.SessionId ||
                current.ArtifactDigest != basis.ArtifactDigest || current.PriorDecisionId != prior.Id ||
                current.PriorRevision != prior.Revision || !basis.Allows(decision))
                return $"The issuing authority must dispose of the recovered Story under decision {prior.Id:D}. {decision?.Rationale}";
        }
        return null;
    }

    private static async Task<ProgressiveAdvance> RecoverStoryRequestAsync(Guid boardId, GameProductionPlanningCycleV1 cycle,
        AgentCoordinationSession authority, ProgressiveEvidence evidence, GameTechnicalDeliveryProposalV1 outline,
        ProgressivePlanningProgress progress, string direction, OwnershipReview? ownership, AgentRuntimeContext context, CancellationToken token)
    {
        var matches = new List<StoryAttempt>();
        void Consider(AgentCoordinationSession session)
        {
            if (session.SourceKind != "Board" || session.BoardSource?.BoardId != boardId ||
                session.Initiator.OrganizationUserId != authority.Initiator.OrganizationUserId ||
                session.Target.OrganizationUserId != authority.Target.OrganizationUserId) return;
            var artifact = session.Turns.SingleOrDefault(t => t.Ordinal == 0 &&
                t.SpeakerOrganizationUserId == authority.Initiator.OrganizationUserId)?.Artifact;
            if (artifact is not { Type: "video-game.production.planning-cycle.v1", IsFinalPage: true } ||
                artifact.Key != cycle.PlanningFingerprint || !artifact.Payload.TryGetProperty("progressivePlanning", out var payload)) return;
            var scope = payload.Deserialize<ProgressiveRequest>(ProgressiveJson);
            if (scope is not { Phase: "Story", StoryKey: string key } || scope.ScopeKey != evidence.ScopeKey ||
                scope.OutlineDigest != evidence.OutlineDigest || scope.DirectionsDigest != evidence.DirectionsDigest ||
                scope.Adaptive != evidence.Adaptive || !(scope.DetailedStoryKeys ?? []).ToHashSet().SetEquals(progress.DetailedStoryKeys) ||
                progress.DetailedStoryKeys.Contains(key) || scope.Adaptive &&
                    (scope.DraftBasis is null || progress.AcceptedBases.GetValueOrDefault(key) != scope.DraftBasis)) return;
            var original = artifact.Payload.Deserialize<GameProductionPlanningCycleV1>();
            if (original is null || original.WorkstreamId != cycle.WorkstreamId || original.BoardId != boardId ||
                original.TeamId != cycle.TeamId || original.ApprovedPackageId != cycle.ApprovedPackageId ||
                original.ApprovedPackageVersion != cycle.ApprovedPackageVersion || original.ApprovedPackageDigest != cycle.ApprovedPackageDigest ||
                original.PlanningFingerprint != cycle.PlanningFingerprint) return;
            matches.Add(new(session, artifact, scope));
        }

        if (progress.FailedStorySessionId is Guid failed)
            Consider(await context.Platform.Communication.ReadCoordinationAsync(failed, token));
        else
        {
            Guid? cursor = null;
            do
            {
                var page = await context.Platform.Communication.DiscoverBoardContributionsAsync(new(cycle.WorkstreamId,
                    cycle.TeamId, boardId, authority.Target.OrganizationUserId, "video-game.production.planning-cycle.v1",
                    cycle.PlanningFingerprint, cursor), token);
                foreach (var id in page.SessionIds) Consider(await context.Platform.Communication.ReadCoordinationAsync(id, token));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }

        var valid = new List<StoryAttempt>();
        foreach (var attempt in matches.Where(m => m.Session.Status == "Completed"))
        {
            var result = attempt.Session.Turns.OrderBy(t => t.Ordinal).LastOrDefault(t =>
                t.SpeakerOrganizationUserId == authority.Target.OrganizationUserId &&
                t.Artifact?.Type == "video-game.production.technical-delivery-proposal.v1")?.Artifact;
            if (result is not { IsFinalPage: true } || string.IsNullOrEmpty(result.Digest) || ReadProgressiveEvidence(result) is not { Phase: "Story" } detail) continue;
            var proposal = result.Payload.Deserialize<GameTechnicalDeliveryProposalV1>();
            if (proposal is null) continue;
            try { ValidateProgressiveProposal(outline, evidence, proposal, detail, progress.DetailedStoryKeys, attempt.Scope.StoryKey); }
            catch (InvalidOperationException) { continue; }
            valid.Add(attempt);
        }
        if (valid.Count == 1)
            return new(progress with { RefinementSessionId = valid[0].Session.Id, PendingStoryKey = valid[0].Scope.StoryKey });
        if (matches.Count != 1)
            return new(progress, Blocker: "The board manager must reconcile the original Story request and its competing or missing source-bound attempts.");
        var previous = matches[0];
        var story = previous.Scope.StoryKey!;
        if (previous.Session.Status is "Active" or "Summarizing")
            return new(progress with { RefinementSessionId = previous.Session.Id, PendingStoryKey = story });
        if (previous.Session.Status == "Cancelled")
            return new(progress, Blocker: "The board manager cancelled this Story request. A new direction is required.");
        if ((previous.Scope.ReviewDirectionsDigest ?? previous.Scope.DirectionsDigest) != direction ||
            ownership is not null && OwnershipCandidates(SuccessorCandidates(outline.DeliveryItems, progress.DetailedStoryKeys.ToHashSet()),
                outline.DeliveryItems, ownership).All(x => x.ProposalKey != story))
            return new(progress); // Reread changed direction and scoped holds through the ordinary planning review.
        if (progress.StoryRetries > 5)
            return new(progress, Blocker: "The Technical Director and board manager must resolve the repeated refinement failure: " + progress.StoryRetryReason);
        var request = previous.Request;
        var next = await context.Platform.Communication.StartBoardCoordinationAsync(new(authority.Target.OrganizationUserId, boardId,
            previous.Session.Subject, previous.Session.Objective, previous.Session.SuccessCriteria,
            "Refine the same approved Story. The previous attempt could not be used: " + progress.StoryRetryReason,
            BoundedMutationKey($"story-recovery:{previous.Session.Id:N}:{progress.StoryRetries}"),
            new(request.Type, request.SchemaVersion, request.Key, request.PageOrdinal, request.IsFinalPage, request.Payload)), token);
        return new(progress with { RefinementSessionId = next.Id, PendingStoryKey = story },
            Waiting: "The Technical Director has the corrected request for the same Story.");
    }
}

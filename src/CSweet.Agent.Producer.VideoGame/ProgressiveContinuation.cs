using System.Text.Json;
using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ProgressiveContinuation(string? Fingerprint);
    internal sealed record PlanningContinuationReceipt(string Fingerprint, long FromRevision);

    internal static async Task<ProgressiveContinuation?> ReadProgressiveContinuationAsync(Guid project, Guid board,
        string planningFingerprint, AgentRuntimeContext context, CancellationToken ct)
    {
        var state = await context.Platform.ReadOperatingStateAsync<ProducerOperatingState>(ProjectStateKeys.Portfolio("producer"), ct);
        if (state?.Payload.PlanningCycles.TryGetValue(project, out var cycle) != true || cycle is null || cycle.Progressive is not { } progress ||
            cycle.BoardId != board || cycle.PlanningFingerprint != planningFingerprint) return null;
        var items = (await context.Platform.Work.ReadBoardAsync(board, ct)).Items;
        var stories = items.Where(item => item.Kind == "Story" && item.ProposalProvenance is { } provenance &&
            progress.DetailedStoryKeys.Contains(provenance.ProposalItemKey, StringComparer.Ordinal)).ToArray();
        if (stories.Length != progress.DetailedStoryKeys.Count || stories.Length == 0 ||
            stories.Select(item => item.ProposalProvenance!.ProposalItemKey).Distinct().Count() != stories.Length ||
            stories.Any(story => story.ParentItemId is null || !items.Any(item => item.ParentItemId == story.Id && item.ExecutionMode == WorkItemExecutionModes.Executable) ||
                items.Any(item => item.ParentItemId == story.Id && item.ExecutionMode == WorkItemExecutionModes.Executable && item.Status is not ("Done" or "Completed"))))
            return new(null);
        var epics = stories.Select(item => item.ParentItemId!.Value).ToHashSet();
        var plans = (await context.Platform.Work.ReadDeliveryPlansAsync(new(project), ct))
            .Where(plan => plan.Status == "Completed" && plan.EpicItemIds.Any(epics.Contains)).ToArray();
        if (!epics.IsSubsetOf(plans.SelectMany(plan => plan.EpicItemIds).ToHashSet())) return new(null);
        return new(ProducerPolicyFingerprint.Digest(JsonSerializer.Serialize(new {
            policy = "progressive-continuation-v1", progress.ScopeKey, progress.OutlineDigest,
            stories = stories.Select(item => item.Id).Order(), plans = plans.Select(plan => plan.Id).Order() })));
    }

    private static async Task<bool> AuthorizeContinuationRecoveryAsync(PersonalTodoItem item, string fingerprint,
        AgentRuntimeContext context, CancellationToken ct)
    {
        // The receipt is written first. A crash before Requeue can resume the same
        // revision; another failure consumes that revision and cannot loop on events.
        var key = "producer-continuation:" + item.Id.ToString("N");
        var receipt = await new RevisionSafeProjectState(context.Platform).MergeAsync<PlanningContinuationReceipt>(key,
            "producer.planning-continuation.v1", 1,
            current => current?.Fingerprint == fingerprint ? current : new(fingerprint, item.Revision),
            new Dictionary<string, string>(), "producer-continuation-receipt:" + item.Id.ToString("N") + ":" + fingerprint, ct);
        return receipt.Payload.FromRevision == item.Revision;
    }
}

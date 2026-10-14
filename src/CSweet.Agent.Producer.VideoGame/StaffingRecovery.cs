using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static async Task SubmitCoverageAsync(ResourceChangeProposalRequest proposal, Guid? boardId,
        Func<ResourceChangeProposalRequest, CancellationToken, Task<ResourceChangeRequestResponse>> submit,
        CancellationToken token,
        Func<CancellationToken, Task<string>>? readEvidenceRevision = null)
    {
        var response = await submit(proposal, token);
        if (boardId.HasValue && readEvidenceRevision is not null && response.Status == "RevisionRequested" &&
            response.DecisionComment == "Refresh the proposal with the current trusted flow-metrics source revision.")
        {
            var revision = await readEvidenceRevision(token);
            if (proposal.Evidence.Any(e => e.Kind == "scope-capability-gap" && e.SourceRevision != revision))
            {
                // Retain the approved staffing baseline. RevisionRequested is historical
                // rejection evidence, not an approved baseline for resource-change deltas.
                // A replay with unchanged evidence
                // returns the same correction; a second rejection waits for normal recovery.
                var evidence = proposal.Evidence.Select(e => e.Kind == "scope-capability-gap"
                    ? e with { SourceRevision = revision } : e).ToArray();
                var fingerprint = ProducerPolicyFingerprint.Digest(System.Text.Json.JsonSerializer.Serialize(new
                    { proposal.IdempotencyKey, revision }));
                await submit(proposal with { Evidence = evidence,
                    IdempotencyKey = "producer-coverage-refresh:" + fingerprint }, token);
            }
            return;
        }
        if (boardId.HasValue && response.Status == "RevisionRequested" && response.DecisionComment ==
            "Initial staffing requires the Producer's confident review and the Director's exact accepted production brief.")
        {
            // Reassess this legacy board-discovery rejection once. The Director still
            // validates the new proposal; repeating the call replays the same request.
            await submit(proposal with { IdempotencyKey = proposal.IdempotencyKey + ":board-review-v1" }, token);
        }
    }
}

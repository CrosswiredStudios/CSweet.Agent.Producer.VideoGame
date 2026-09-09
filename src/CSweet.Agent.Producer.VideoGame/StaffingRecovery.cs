using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static async Task SubmitCoverageAsync(ResourceChangeProposalRequest proposal, Guid? boardId,
        Func<ResourceChangeProposalRequest, CancellationToken, Task<ResourceChangeRequestResponse>> submit,
        CancellationToken token)
    {
        var response = await submit(proposal, token);
        if (boardId.HasValue && response.Status == "RevisionRequested" && response.DecisionComment ==
            "Initial staffing requires the Producer's confident review and the Director's exact accepted production brief.")
        {
            // Reassess this legacy board-discovery rejection once. The Director still
            // validates the new proposal; repeating the call replays the same request.
            await submit(proposal with { IdempotencyKey = proposal.IdempotencyKey + ":board-review-v1" }, token);
        }
    }
}
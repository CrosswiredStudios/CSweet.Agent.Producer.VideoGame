using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    // A request to another agent is idempotent: asking again under the same key returns the same session,
    // including one that failed. When a collaborator's attempt fails or returns unusable work, the next
    // retry must ask under a new key. This counts those failures for one exact request.
    internal sealed record RegenerationCount(int Attempts);

    private static string RegenerationKey(string scope) => AgentOperatingStateKey.Create("producer-regeneration:v1", new { scope });

    internal static async Task<int> ReadRegenerationAsync(string scope, AgentRuntimeContext context, CancellationToken token) =>
        (await context.Platform.ReadOperatingStateAsync<RegenerationCount>(RegenerationKey(scope), token))?.Payload.Attempts ?? 0;

    internal static async Task RecordRegenerationAsync(string scope, int failedAttempt, AgentRuntimeContext context, CancellationToken token) =>
        await DurableAgentState.UpdateAsync<RegenerationCount>(context.Platform, RegenerationKey(scope), "producer.regeneration.v1", 1,
            prior => new(Math.Max(prior?.Attempts ?? 0, failedAttempt + 1)), token);

    internal static string RegenerationSuffix(int attempt) => attempt > 0 ? ":retry-" + attempt : "";

    private static string CollaboratorDiagnostic(AgentCoordinationSession session)
    {
        var text = session.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId &&
            x.Disposition is "Blocked" or "Failed")?.Content ?? session.FinalSummary ?? session.Status;
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500];
    }
}

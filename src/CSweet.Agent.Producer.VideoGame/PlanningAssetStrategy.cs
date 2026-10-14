using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static async Task<IReadOnlyList<string>> ReadAssetPlanningDirectionsAsync(
        IReadOnlyList<DecisionRecord> decisions, Guid project, AgentRuntimeContext context, CancellationToken token)
    {
        var directions = new List<string>();
        foreach (var decision in decisions.Where(d => d.WorkstreamId == project &&
            d.TypeKey == "video-game.asset-strategy.v1" && d.AuthorityRuleKey == "routine-project-production-strategy" &&
            d.Status == DecisionStatuses.Decided && d.SupersededByDecisionId is null).OrderBy(d => d.Id))
        {
            var sources = decision.Evidence.Where(e => e.Kind == "artifact" &&
                e.TypeKey == "video-game.vision.v1" && e.Status == "Accepted").ToArray();
            if (sources.Length != 1 || decision.SelectedOptionId is not ("procedural" or "provided" or "generative" or "hybrid")) continue;
            var evidence = sources[0];
            var document = await context.Platform.Artifacts.GetAsync(evidence.ResourceId, token);
            var revision = document.Revisions.SingleOrDefault(r => r.Id == evidence.RevisionId);
            if (document.WorkstreamId != project || document.DocumentType != evidence.TypeKey ||
                document.AcceptedRevisionId != evidence.RevisionId || revision?.Status != "Accepted" ||
                revision.ContentSha256 != evidence.Digest) continue;
            if (decision.TypeData is not { ValueKind: JsonValueKind.Object } strategy ||
                strategy.GetRawText().Length > 16000) continue;
            var modes = strategy.EnumerateObject().Where(p => p.Name.Equals("mode", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (modes.Length != 1 || modes[0].Value.ValueKind != JsonValueKind.String ||
                modes[0].Value.GetString() != decision.SelectedOptionId) continue;
            // The recorded strategy is project data, never a new grant or scope amendment.
            directions.Add($"Asset strategy decision {decision.Id:D}, revision {decision.Revision}: " +
                $"{decision.SelectedOptionId}. Configuration: {strategy.GetRawText()}. " +
                "Plan asset work using this production method while preserving the accepted quality, content and licensing criteria. " +
                "For procedural mode, define reproducible code-native visual and audio generation and justify any separate specialist ownership; " +
                "do not require externally authored media merely because older brief text names sprites or tracks. " +
                "An unresolved conflict remains an explicit planning question; this decision does not authorize dropping deliverables or reviews.");
        }
        return directions;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static (string Message, AgentCoordinationArtifactSubmission Artifact) BoundPlanningHandoff(
        string message, AgentCoordinationArtifactSubmission artifact)
    {
        if (message.Length <= 32768) return (message, artifact);
        // The artifact and first message are persisted atomically by coordination start.
        // Keep the full text in the authenticated planning-cycle artifact, without trimming evidence.
        var payload = JsonNode.Parse(artifact.Payload.GetRawText())!.AsObject();
        payload["coordinationContext"] = message;
        var expanded = JsonSerializer.SerializeToElement(payload);
        if (expanded.GetRawText().Length > 65536)
            throw new InvalidOperationException("Planning coordination exceeds both message and artifact bounds; no scope was truncated.");
        return ("The complete Producer planning request and recorded decisions are in this planning-cycle artifact's " +
            "coordinationContext field. Read that exact context before proposing changes; the short message does not replace it.",
            artifact with { Payload = expanded });
    }
}

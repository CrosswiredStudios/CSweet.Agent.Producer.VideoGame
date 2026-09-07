using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CrosswiredStudios.VideoGame.PitchCollaboration;

internal sealed record PitchBrief(GameVisionBrief Vision, Guid PitchArtifactId, Guid PitchRevisionId, string PitchSha256)
{
    public IReadOnlyList<CollaborationDocumentReference> DocumentReferences => new[]
    {
        new CollaborationDocumentReference(PitchArtifactId, PitchRevisionId, PitchSha256),
        new CollaborationDocumentReference(Vision.HighLevelGddArtifactId!.Value, Vision.HighLevelGddAcceptedRevisionId!.Value, Vision.HighLevelGddRevisionSha256!)
    }.DistinctBy(x => x.DocumentId).ToArray();
}
internal sealed record PitchReview(string PitchDigest, Guid DocumentId, Guid RevisionId, string RevisionSha256,
    bool Ready, IReadOnlyList<string> Questions, string Rationale)
{
    public IReadOnlyList<CollaborationDocumentReference> DocumentReferences => [new(DocumentId, RevisionId, RevisionSha256)];
}
internal sealed record PitchReply(string PitchDigest, Guid DocumentId, Guid RevisionId, string RevisionSha256,
    bool Accepted, string Guidance, string SuggestedMarkdown);
internal sealed record ProducerReview(bool Ready, IReadOnlyList<string> Questions, string Rationale, string DraftMarkdown);
internal sealed record DirectorReview(bool Accept, string Guidance, string SuggestedMarkdown);

internal static class PitchProtocol
{
    public const string BriefType = "video-game.production.pitch-brief.v1";
    public const string ReviewType = "video-game.production.pitch-review.v1";
    public const string ReplyType = "video-game.production.pitch-reply.v1";
    public const string DocumentType = "video-game.production-plan.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool CanPlan(ProducerReview review) => CollaborationActions.IsReady(review.Ready, review.Questions, [], review.Rationale) && ValidDocument(review.DraftMarkdown);
    public static bool ValidDocument(string? markdown) => !string.IsNullOrWhiteSpace(markdown) && markdown.Length <= 192000 &&
        new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }
            .All(heading => markdown.Contains($"## {heading}", StringComparison.OrdinalIgnoreCase));
    public static bool Matches(PitchReview review, PitchReply reply) => review.PitchDigest == reply.PitchDigest && CollaborationActions.CanAcceptHandoff(
            new(new(review.DocumentId, review.RevisionId, review.RevisionSha256), review.Ready, review.Questions, [], review.Rationale),
            new(new(reply.DocumentId, reply.RevisionId, reply.RevisionSha256), reply.Accepted, reply.Guidance, []));
    public static AgentCoordinationArtifactSubmission Artifact<T>(string type, string key, T value)
    {
        var artifact = new AgentCoordinationArtifactSubmission(type, "1.0", key, 1, true, JsonSerializer.SerializeToElement(value, Json));
        return value switch
        {
            PitchBrief brief => CollaborationActions.WithDocuments(artifact, brief.DocumentReferences),
            PitchReview review => CollaborationActions.WithDocuments(artifact, review.DocumentReferences),
            _ => artifact
        };
    }

    // Cache model decisions before any document mutation so duplicate deliveries cannot change the decision.
    public static async Task<T> CachedAsync<T>(string key, AgentRuntimeContext context, Func<Task<T>> create, CancellationToken token) where T : class
    {
        var prior = await context.Platform.ReadOperatingStateAsync<T>(key, token);
        if (prior is not null) return prior.Payload;
        var value = await create();
        try
        {
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(key,
                "video-game.pitch-turn.v1", 1, "Active", new Dictionary<string, string>(), [], key, [], Guid.NewGuid(),
                JsonSerializer.SerializeToElement(value), null, key), token);
        }
        catch (PlatformCapabilityException ex) when (ex.Code == PlatformCapabilityErrorCode.Conflict)
        {
            var winner = await context.Platform.ReadOperatingStateAsync<T>(key, token);
            if (winner is null) throw;
            return winner.Payload;
        }
        return value;
    }

    public static Task<(ArtifactDocument Document, ArtifactRevision Revision)> ReadAcceptedAsync(
        Guid documentId, Guid revisionId, string digest, AgentRuntimeContext context, CancellationToken token) =>
        context.Platform.Artifacts.ReadAcceptedAsync(new(documentId, revisionId, digest), token);
}

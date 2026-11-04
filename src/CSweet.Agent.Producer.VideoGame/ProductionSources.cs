using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private static Task<CollaborationDocumentReference> EnsureProductionSourcesAsync(Guid project, Guid team, Guid manager,
        ArtifactDocument pitch, ArtifactRevision pitchRevision, ArtifactDocument gdd, ArtifactRevision gddRevision,
        AgentRuntimeContext context, CancellationToken token)
    {
        var sources = new ProductionSources(1, project, team,
            new(pitch.Id, pitchRevision.Id, pitchRevision.ContentSha256, pitch.DocumentType, pitchRevision.Content),
            new(gdd.Id, gddRevision.Id, gddRevision.ContentSha256, gdd.DocumentType, gddRevision.Content));
        var content = sources.Serialize();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var key = $"production-sources:{project:N}:{digest}";
        return PitchProtocol.CachedAsync(key, context, async () =>
        {
            var document = await context.Platform.Artifacts.CreateAsync(new CreateArtifactDocument(
                "Exact approved production sources", content, ProductionSources.DocumentType, key,
                StewardOrganizationUserId: manager) { WorkstreamId = project, TeamId = team }, token);
            var revision = document.Revisions.Single(x => x.Id == document.LatestRevisionId);
            await context.Platform.Artifacts.SubmitAsync(new SubmitArtifactRevision(document.Id, revision.Id,
                $"production-sources-submit:{revision.Id:N}", ReviewerOrganizationUserId: manager), token);
            return new CollaborationDocumentReference(document.Id, revision.Id, revision.ContentSha256);
        }, token);
    }
}

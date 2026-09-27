using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PlanningHandoffTests
{
    private static AgentCoordinationArtifactSubmission Artifact() => new("planning-cycle", "1.0", "cycle", 1, true,
        JsonSerializer.SerializeToElement(new { documentReferences = new[] { new { documentId = Guid.NewGuid(), revisionId = Guid.NewGuid() } } }));

    [Theory]
    [InlineData(32768, false)]
    [InlineData(32769, true)]
    public void ExactBoundaryPreservesAllTextAndDocumentReferences(int length, bool stored)
    {
        var artifact = Artifact();
        var text = new string('x', length - 1) + "≥";
        var result = SpecialistAgent.BoundPlanningHandoff(text, artifact);
        Assert.InRange(result.Message.Length, 1, 32768);
        Assert.Equal(stored, result.Artifact.Payload.TryGetProperty("coordinationContext", out var context));
        Assert.Equal(text, stored ? context.GetString() : result.Message);
        Assert.Equal(artifact.Payload.GetProperty("documentReferences").GetRawText(),
            result.Artifact.Payload.GetProperty("documentReferences").GetRawText());
    }

    [Fact]
    public void ArtifactLimitIncludesEscapingAndRejectsWithoutTruncating()
    {
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BoundPlanningHandoff(new string('"', 40000), Artifact()));
    }
}

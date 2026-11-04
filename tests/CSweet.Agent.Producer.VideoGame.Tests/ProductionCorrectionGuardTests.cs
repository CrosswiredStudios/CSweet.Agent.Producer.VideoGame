using CSweet.Agent.SDK;
using System.Text.Json;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ProductionCorrectionGuardTests
{
    [Theory]
    [InlineData("progress", true)]
    [InlineData("unchanged", false)]
    [InlineData("drop", false)]
    [InlineData("downgrade", false)]
    [InlineData("wrong-hash", false)]
    [InlineData("third", false)]
    [InlineData("foreign-manager", false)]
    public void FurtherCorrectionsRequireProgressAndRemainBounded(string fault, bool expected)
    {
        var manager = Guid.NewGuid(); var producer = Guid.NewGuid(); var document = Guid.NewGuid();
        var transcript = new List<AgentCoordinationTurn>();
        var open = new CreativeIssue("controls", "controls-accessibility", "blocking", "open", "Keyboard alternative", "Missing input rule", "game-producer", "pre-production", "Specify the keyboard input");
        var other = open with { Id = "playtest", CheckId = "validation-plan", Requirement = "Observe repeat-play intent" };
        void Add(Guid speaker, AgentCoordinationArtifactSubmission artifact) => transcript.Add(new(Guid.NewGuid(), transcript.Count,
            speaker, "Continue", "Exact review", DateTimeOffset.UtcNow, new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "transport")));
        PitchReview Review(string hash) => new("pitch", document, Guid.NewGuid(), hash, true, [], "Ready");
        void Draft(PitchReview r) => Add(producer, PitchProtocol.Artifact(PitchProtocol.ReviewType, "pitch", r));
        void Reply(PitchReview r, CreativeIssue[] issues, Guid? speaker = null) => Add(speaker ?? manager, PitchProtocol.Artifact(PitchProtocol.ReplyType, "pitch",
            new PitchReply("pitch", document, r.RevisionId, r.RevisionSha256, false, "Apply findings", "") { Correction = ProductionCorrection.Create(r.RevisionSha256, issues) }));
        var first = Review("first");
        for (var i = 0; i < 4; i++) Draft(first);
        Reply(first, [open, other]);
        var next = Review(fault == "unchanged" ? "first" : "second"); Draft(next);
        var issues = fault switch {
            "drop" => new[] {other},
            "downgrade" => new[] {open with {Severity = "advisory", Status = "resolved"}, other},
            _ => new[] {open with {Status = "resolved", Evidence = "Keyboard rule now present"}, other}
        };
        Reply(next, issues, fault == "foreign-manager" ? Guid.NewGuid() : null);
        if (fault == "third") { var third = Review("third"); Draft(third); Reply(third, [open with {Status="resolved"}, other]); next = third; }
        var reply = transcript[^1].Artifact!.Payload.Deserialize<PitchReply>(PitchProtocol.Json)!;
        if (fault == "wrong-hash") reply = reply with { Correction = ProductionCorrection.Create("foreign", issues) };
        Assert.Equal(expected, SpecialistAgent.CanCorrectProductionBrief(transcript, manager, producer, next, reply, -1));
    }
}

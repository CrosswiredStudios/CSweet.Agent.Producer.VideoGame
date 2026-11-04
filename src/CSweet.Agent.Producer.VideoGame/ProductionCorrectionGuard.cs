using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;
using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const int MaximumCreativeCorrections = 2;

    internal static bool CanCorrectProductionBrief(IReadOnlyList<AgentCoordinationTurn> transcript, Guid manager, Guid producer,
        PitchReview? previous, PitchReply? reply, int decisionStart)
    {
        if (previous is not { Ready: true, Questions.Count: 0 } || reply?.Correction is not { } correction ||
            !correction.Valid(previous.RevisionSha256)) return false;
        var replies = new List<PitchReply>();
        var reviewsSeen = 0;
        foreach (var turn in transcript.Skip(decisionStart + 1))
        {
            if (turn.SpeakerOrganizationUserId == producer && turn.Artifact?.Type == PitchProtocol.ReviewType &&
                turn.Artifact.Payload.Deserialize<PitchReview>(PitchProtocol.Json)?.PitchDigest == previous.PitchDigest) reviewsSeen++;
            if (reviewsSeen < MaximumPitchReviews || turn.SpeakerOrganizationUserId != manager || turn.Artifact?.Type != PitchProtocol.ReplyType) continue;
            var value = turn.Artifact.Payload.Deserialize<PitchReply>(PitchProtocol.Json);
            if (value?.PitchDigest == previous.PitchDigest && value.DocumentId == previous.DocumentId && value.Correction is not null) replies.Add(value);
        }
        if (!replies.Any(x => x.RevisionId == previous.RevisionId && x.RevisionSha256 == previous.RevisionSha256 &&
            x.Correction!.ReviewDigest == correction.ReviewDigest)) return false;
        // Count distinct receipts so duplicate delivery cannot consume or reset the allowance.
        var corrections = replies.DistinctBy(x => x!.Correction!.ReviewDigest).ToArray();
        if (corrections.Length > MaximumCreativeCorrections) return false;
        var prior = corrections.LastOrDefault(x => x!.Correction!.ReviewDigest != correction.ReviewDigest);
        if (prior is null) return true; // First exact, actionable review after the ordinary draft budget.
        if (prior.RevisionSha256 == previous.RevisionSha256 || !prior.Correction!.Valid(prior.RevisionSha256)) return false;
        var priorBlockers = CreativeChecklist.Blockers(prior.Correction.Issues);
        return priorBlockers.All(p => correction.Issues.Any(x => x.Id == p.Id && x.Requirement == p.Requirement && x.Severity == "blocking")) &&
            priorBlockers.Any(p => correction.Issues.Any(x => x.Id == p.Id && x.Requirement == p.Requirement && x.Status == "resolved"));
    }
}

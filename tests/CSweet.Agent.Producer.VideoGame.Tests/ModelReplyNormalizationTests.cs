using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ModelReplyNormalizationTests
{
    private static readonly string[] Criteria = ["Movement works with keyboard", "Score is shown"];

    private static DeliveryAcceptanceDecision Decision(bool approved, params (string Criterion, bool Satisfied)[] reviews) =>
        new(approved, "Delivery reviewed.", [], reviews.Select(x => new DeliveryCriterionReview(x.Criterion, x.Satisfied, "Observed in the QA report")).ToArray());

    [Fact]
    public void Approval_with_an_unsatisfied_criterion_becomes_a_rejection_with_that_finding()
    {
        var result = SpecialistAgent.NormalizeAcceptance(Decision(true, (Criteria[0], true), (Criteria[1], false)), Criteria);
        Assert.False(result.Approved);
        Assert.Equal("Score is shown: Observed in the QA report", Assert.Single(result.Findings));
    }

    [Fact]
    public void Lightly_reworded_criteria_are_mapped_to_the_exact_text()
    {
        var result = SpecialistAgent.NormalizeAcceptance(Decision(true, ("1. movement works with keyboard.", true), ("- Score  is shown", true)), Criteria);
        Assert.True(result.Approved);
        Assert.Equal(Criteria.Order(), result.Criteria.Select(x => x.Criterion).Order());
    }

    [Fact]
    public void Missing_or_invented_criteria_are_named()
    {
        var error = Assert.Throws<AgentModelJsonException>(() =>
            SpecialistAgent.NormalizeAcceptance(Decision(true, (Criteria[0], true), ("Runs on mobile", true)), Criteria));
        Assert.Contains("\"Runs on mobile\" is not one of the supplied acceptance criteria", error.Message);
        Assert.Contains("criterion \"Score is shown\" is not reviewed", error.Message);
    }

    [Fact]
    public void Approval_with_findings_and_rejection_without_reasons_are_sent_back()
    {
        var withFindings = Decision(true, (Criteria[0], true), (Criteria[1], true)) with { Findings = ["Tidy the HUD"] };
        Assert.Contains("approved with findings", Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeAcceptance(withFindings, Criteria)).Message);
        var bare = Decision(false, (Criteria[0], true), (Criteria[1], true));
        Assert.Contains("rejection needs at least one actionable finding", Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeAcceptance(bare, Criteria)).Message);
    }

    [Fact]
    public void Role_replanning_names_unsatisfied_criteria_and_is_never_approved()
    {
        var decision = Decision(true, (Criteria[0], false), (Criteria[1], true)) with
            { RequiresRoleReplanning = true, RoleRepairCriteria = ["movement works with keyboard"] };
        var result = SpecialistAgent.NormalizeAcceptance(decision, Criteria);
        Assert.False(result.Approved);
        Assert.Equal(Criteria[0], Assert.Single(result.RoleRepairCriteria!));
        var satisfied = decision with { RoleRepairCriteria = [Criteria[1]] };
        Assert.Contains("is marked satisfied", Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeAcceptance(satisfied, Criteria)).Message);
    }

    private static ProducerReview Review(bool ready, params string[] questions) => new(ready, questions, "Delivery approach is clear.",
        string.Join("\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }
            .Select(h => $"## {h}\nConcrete content."))) ;

    [Fact]
    public void Open_questions_outweigh_a_ready_label()
    {
        var result = SpecialistAgent.NormalizeProducerReview(Review(true, "Which art style?"));
        Assert.False(result.Ready);
        Assert.Equal("Which art style?", Assert.Single(result.Questions));
    }

    [Fact]
    public void An_incomplete_brief_names_the_missing_headings()
    {
        var review = Review(false, "Which art style?") with { DraftMarkdown = "## Scope\nOnly scope." };
        var error = Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeProducerReview(review));
        Assert.Contains("## Player experience", error.Message);
        Assert.Contains("## Open questions", error.Message);
    }

    [Fact]
    public void Not_ready_without_questions_is_sent_back()
    {
        var error = Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeProducerReview(Review(false, " ")));
        Assert.Contains("not ready needs at least one concrete planning question", error.Message);
    }

    private static WorkItem Item(string status = "Ready") => JsonSerializer.Deserialize<WorkItem>("{}")! with
    {
        Id = Guid.NewGuid(), Status = status, Identifier = "VG-7", Planning = new(["Keep the full game"], ["Independent QA"], [])
    };

    [Fact]
    public void Scope_replacements_must_copy_one_current_entry_on_unfinished_work()
    {
        var item = Item(); var done = Item("Done");
        var valid = new ScopeAmendmentPlan([new(item.Id, "acceptanceCriteria", "Independent QA", "Independent QA; device checks deferred")]);
        Assert.Single(SpecialistAgent.NormalizeScopeAmendment(valid, [item, done]).Replacements);
        var invalid = new ScopeAmendmentPlan([
            new(item.Id, "acceptanceCriteria", "independent qa", "Changed"),
            new(done.Id, "requirements", "Keep the full game", "Smaller game"),
            new(item.Id, "notes", "x", "y"),
            new(Guid.NewGuid(), "requirements", "a", "b")]);
        var message = Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.NormalizeScopeAmendment(invalid, [item, done])).Message;
        Assert.Contains("original must be copied exactly", message);
        Assert.Contains("changes finished or cancelled work", message);
        Assert.Contains("field must be requirements, acceptanceCriteria or constraints", message);
        Assert.Contains("which is not on this board", message);
        Assert.Contains("replacements is empty", Assert.Throws<AgentModelJsonException>(() =>
            SpecialistAgent.NormalizeScopeAmendment(new ScopeAmendmentPlan([]), [item])).Message);
    }
}

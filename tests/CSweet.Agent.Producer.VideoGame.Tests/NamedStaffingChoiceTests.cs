using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class NamedStaffingChoiceTests
{
    [Fact]
    public void LivePositionalReplyCannotSelectAnotherRoleUnderTheNamedContract()
    {
        var reply = JsonSerializer.Deserialize<SpecialistAgent.CoverageAssessment>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "qfn-kickoff-positional-role.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var catalog = SpecialistAgent.CoverageRoleCatalog(Array.Empty<string>());
        Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.ValidateCoverage(reply, catalog));
        var corrected = reply with { Gaps = [new("TechnicalDirector", reply.Gaps[0].Work)] };
        foreach (var choices in new[] { catalog, catalog.Reverse().ToArray() })
        {
            SpecialistAgent.ValidateCoverage(corrected, choices);
            Assert.Equal("game-technical-director", choices.Single(x => x.Capability == corrected.Gaps[0].Capability).RoleKey);
        }
        Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.ValidateCoverage(corrected with { Gaps = [new("16", "Technical planning")] }, catalog));
    }

    [Theory]
    [InlineData("hire")]
    [InlineData("consult")]
    public void SprintReviewsSelectNamedCapabilitiesRegardlessOfCatalogOrder(string kind)
    {
        var choice = new SpecialistAgent.ReviewChoice("Investigate mechanics", 0, false, true, "New evidence", "No new playable evidence",
            [new(kind, "Resolve interacting power-ups", "GameDesigner", [0])]);
        var evidence = new[] { new SpecialistAgent.ReviewFact("brief", Guid.NewGuid(), "approved", "Approved rules") };
        var catalog = SpecialistAgent.CoverageRoleCatalog(Array.Empty<string>());
        Assert.Equal(choice, SpecialistAgent.ValidateReview(choice, 1, catalog.Reverse().ToArray(), evidence));
        Assert.Throws<AgentModelJsonException>(() => SpecialistAgent.ValidateReview(choice with {
            Actions = [new(kind, "Resolve interacting power-ups", "0", [0])] }, 1, catalog, evidence));
    }
}

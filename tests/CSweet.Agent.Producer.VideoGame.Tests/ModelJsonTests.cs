using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ModelJsonTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Decision = """{"approved":true,"summary":"ok","findings":[],"criteria":[{"criterion":"a","satisfied":true,"evidence":"e"}]}""";

    [Theory]
    [InlineData(Decision)]
    [InlineData("```json\n" + Decision + "\n```")]
    [InlineData("```\n" + Decision + "\n```\n")]
    [InlineData("  \n```json\r\n" + Decision + "\r\n```  ")]
    [InlineData("Here is my review:\n" + Decision + "\nLet me know if you need more.")]
    public void ReadsTheDecisionWhetherOrNotTheModelFencedIt(string reply)
    {
        var decision = ModelJson.Deserialize<DeliveryAcceptanceDecision>(reply, Json, "Producer review");
        Assert.True(decision.Approved);
        Assert.Equal("a", Assert.Single(decision.Criteria).Criterion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("```json\n```")]
    [InlineData("I approve this ticket.")]
    [InlineData("```json\n{\"approved\": tru\n```")]
    public void UnreadableRepliesBecomeAReportableWaitReason(string reply)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelJson.Deserialize<DeliveryAcceptanceDecision>(reply, Json, "Producer review"));
        Assert.StartsWith("Producer review returned", error.Message);
    }
}

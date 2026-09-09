namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class CommitmentKeyTests
{
    [Theory]
    [InlineData(140)]
    [InlineData(141)]
    [InlineData(142)]
    [InlineData(160)]
    public void Keys_fit_host_limit_preserving_existing_keys_and_retry_identity(int length)
    {
        var correlation = new string('a', length);
        var key = SpecialistAgent.CommitmentIdempotencyKey(correlation);
        Assert.InRange(key.Length, 1, 160);
        Assert.Equal(key, SpecialistAgent.CommitmentIdempotencyKey(correlation));
        Assert.NotEqual(key, SpecialistAgent.CommitmentIdempotencyKey(correlation[..^1] + "b"));
        if (("producer-commitment:" + correlation).Length <= 160)
            Assert.Equal("producer-commitment:" + correlation, key);
    }

    [Fact]
    public void Long_staffing_role_does_not_prevent_followup_creation()
    {
        var correlation = $"producer-staffing-gap:{Guid.NewGuid():N}:game-technical-director:{new string('a', 64)}";
        Assert.True(("producer-commitment:" + correlation).Length > 160);
        Assert.InRange(SpecialistAgent.CommitmentIdempotencyKey(correlation).Length, 1, 160);
        Assert.InRange(correlation.Length, 1, 160);
    }
}
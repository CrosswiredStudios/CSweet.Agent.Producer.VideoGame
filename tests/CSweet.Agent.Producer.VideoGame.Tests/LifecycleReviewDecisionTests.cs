namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class LifecycleReviewDecisionTests
{
    [Theory]
    [InlineData("refine")]
    [InlineData("wait")]
    public void ExistingCandidatesDoNotForceSelectionWhenManagerNeedsMoreEvidence(string disposition)
    {
        var choice=new SpecialistAgent.ReviewChoice("Resolve system interaction",null,false,true,"New technical evidence",
            "No playable claim",[new("refine","Existing estimate excludes interactions",null,[0])]) {Disposition=disposition};
        var accepted=SpecialistAgent.ValidateReview(choice,2,[],[new("brief",Guid.NewGuid(),"approved","Approved scope")]);
        Assert.Null(accepted.NextCandidate);Assert.Equal(disposition,accepted.Disposition);
        Assert.Throws<CSweet.Agent.SDK.AgentModelJsonException>(()=>SpecialistAgent.ValidateReview(choice with {NextCandidate=0},2,[],
            [new("brief",Guid.NewGuid(),"approved","Approved scope")]));
    }
}

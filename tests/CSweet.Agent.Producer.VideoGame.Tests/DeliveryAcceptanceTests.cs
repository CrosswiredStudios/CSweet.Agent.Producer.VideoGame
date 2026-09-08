using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class DeliveryAcceptanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Sha = new('a', 40);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ReviewCachesDecisionBeforeSubmissionAndReplaysAfterLostResponse(bool approved, bool documentDelivery)
    {
        var boardId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var stages = CodeStages();
        if (documentDelivery)
        {
            var worker = stages[0];
            stages = [worker with { LastOutcomeCode = "completed", LatestOutcome = worker.LatestOutcome! with {
                OutcomeCode = "completed", Output = JsonSerializer.SerializeToElement(new {
                    ArtifactId = documentId, RevisionId = revisionId, Sha256 = "exact-document-digest" }) } }];
        }
        var review = Stage("producer-review", "", Guid.NewGuid()) with { Status = "WaitingForApproval" };
        var item = new WorkItem(Guid.NewGuid(), Guid.NewGuid(), null, null, "Task", "Move player", "Deliver movement",
            "WaitingForApproval", "High", null, 0, 1, null)
        { Planning = new WorkItemPlanningSpecification(["Movement"], ["Input changes position"], []) };
        var itemExecution = new WorkItemExecutionResponse(Guid.NewGuid(), item.Id, "GAME-1", "producer-review", 0,
            "WaitingForApproval", null, stages, DateTimeOffset.UtcNow);
        var execution = new WorkSprintExecutionResponse(Guid.NewGuid(), boardId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [itemExecution]);
        AgentOperatingStateResponse? stored = null;
        var evaluations = 0;
        var submissions = new List<DecideWorkApprovalStageRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (request, _) =>
            {
                Assert.Equal(documentId, request.GetProperty("artifactId").GetGuid());
                return Task.FromResult(new ArtifactDocument(documentId, "Movement design", "game.design", "Submitted",
                    revisionId, revisionId, null, null, item.Id,
                    [new(revisionId, 1, null, "Actual movement design content.", "exact-document-digest", "Submitted", DateTimeOffset.UtcNow, null, null)]));
            })
            .RegisterCapability<WorkItemReference, WorkItem>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(item))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(stored)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    stored = new(Guid.NewGuid(), request.StateKey, "test", 1, "Active", new Dictionary<string,string>(), [],
                        request.StateKey, [], Guid.NewGuid(), request.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    return Task.FromResult(stored);
                })
            .RegisterCapability<DecideWorkApprovalStageRequest, WorkStageExecutionResponse>(WorkOrchestrationCapabilities.DecideApproval,
                (request, _) =>
                {
                    Assert.NotNull(stored);
                    submissions.Add(request);
                    if (submissions.Count == 1) throw new InvalidOperationException("Response lost after submit.");
                    return Task.FromResult(review with { LastOutcomeCode = request.Approved ? "approved" : "rejected" });
                });
        Task<DeliveryAcceptanceDecision> Evaluate(DeliveryAcceptanceInput input, CancellationToken token)
        {
            evaluations++;
            Assert.Equal(documentDelivery ? 1 : 5, input.Stages.Count);
            if (documentDelivery) Assert.Equal("Actual movement design content.", input.DocumentContent);
            else Assert.Null(input.DocumentContent);
            return Task.FromResult(new DeliveryAcceptanceDecision(approved, "Delivery reviewed.",
                approved ? [] : ["Fix movement bounds."], [new("Input changes position", approved, "QA movement report for exact commit.")]));
        }
        await Assert.ThrowsAnyAsync<Exception>(() => SpecialistAgent.ReviewDeliveryAsync(boardId, execution,
            itemExecution, review, runtime.CreateContext(), Evaluate, CancellationToken.None));
        await SpecialistAgent.ReviewDeliveryAsync(boardId, execution, itemExecution, review, runtime.CreateContext(), Evaluate, CancellationToken.None);
        Assert.Equal(1, evaluations);
        Assert.Equal(2, submissions.Count);
        Assert.Equal(submissions[0], submissions[1]);
        Assert.Equal(approved, submissions[1].Approved);
    }

    [Theory]
    [InlineData("missing-qa")]
    [InlineData("wrong-commit")]
    [InlineData("author-qa")]
    [InlineData("unmerged")]
    [InlineData("old-traversal")]
    public void CodeAcceptanceRequiresCurrentIndependentExactCandidateEvidence(string condition)
    {
        var stages = CodeStages().ToList();
        var qa = stages.Single(x => x.StageKey == "quality");
        stages.Remove(qa);
        if (condition != "missing-qa") stages.Add(condition switch
        {
            "wrong-commit" => qa with { LatestOutcome = qa.LatestOutcome! with { Evidence = [new("commit", "Other candidate", new string('b',40))] } },
            "author-qa" => qa with { AgentInstallationId = stages.Single(x => x.StageKey == "specialist-execution").AgentInstallationId },
            "old-traversal" => qa with { Traversal = 1 },
            _ => qa
        });
        if (condition == "unmerged") stages.RemoveAll(x => x.StageKey == "governed-merge");
        var item = new WorkItemExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "GAME-1", "producer-review", 0,
            "WaitingForApproval", null, stages, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.DeliveryEvidence(item, Stage("producer-review", "", Guid.NewGuid())));
    }

    [Fact]
    public void AcceptanceCannotOmitCriteriaOrApproveUnsatisfiedCriteria()
    {
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateAcceptance(new(true, "Done", [], []), ["Movement"]));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateAcceptance(new(true, "Done", [],
            [new("Movement", false, "Not verified")]), ["Movement"]));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateAcceptance(new(false, "Incomplete", [],
            [new("Movement", false, "Missing")]), ["Movement"]));
    }

    private static IReadOnlyList<WorkStageExecutionResponse> CodeStages() =>
        [Stage("specialist-execution", "code-published", Guid.NewGuid()), Stage("technical-review", "approved", Guid.NewGuid()),
         Stage("quality", "passed", Guid.NewGuid()), Stage("merge-decision", "approved", Guid.NewGuid()), Stage("governed-merge", "merged", Guid.NewGuid())];

    private static WorkStageExecutionResponse Stage(string key, string outcome, Guid installation)
    {
        var id = Guid.NewGuid();
        return new(id, key, "AgentExecution", 0, "Completed", "AgentInstallation", Guid.NewGuid(), installation,
            null, 1, outcome, "Evidence", null, null, DateTimeOffset.UtcNow)
        { LatestOutcome = new(id, Guid.NewGuid(), "Completed", outcome, "Evidence", JsonSerializer.SerializeToElement(new { test = "movement" }),
            [new("commit", "Exact candidate", Sha)], []) };
    }
}

using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PitchRefinementTests
{
    [Fact]
    public async Task Refinement_retains_early_guidance_and_long_message_tails_without_a_task_output_cap()
    {
        var f = new Fixture();
        const string first = "Early direction must remain available";
        const string tail = "Critical direction beyond the old 4000 character cutoff";
        f.Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) =>
        {
            Assert.Equal(JsonValueKind.Null, request.GetProperty("maxOutputTokens").ValueKind);
            var prompt = request.GetProperty("messages")[1].GetProperty("text").GetString()!;
            Assert.Contains(first, prompt);
            Assert.Contains(tail, prompt);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant",
                text = JsonSerializer.Serialize(new ProducerReview(true, [], "Full scope considered.", Markdown), PitchProtocol.Json) }));
        });
        var initial = f.Request(1);
        var history = Enumerable.Range(0, 12).Select(i => new AgentCoordinationTurn(Guid.NewGuid(), i + 1,
            f.Director, "Continue", i == 0 ? first + new string('x', 5000) + tail : "Later clarification " + i,
            DateTimeOffset.UtcNow, null)).ToArray();
        var agent = new SpecialistAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                ["llmModel"] = JsonSerializer.SerializeToElement("test-model") }))), f.Context, default);
        Assert.True(configured.Succeeded, configured.Error);
        var result = await agent.HandleCoordinationTurnAsync(initial with { Transcript = [..initial.Transcript, ..history] }, f.Context, default);
        Assert.Equal("Continue", result.Disposition);
        Assert.NotNull(result.Artifact);
    }

    [Fact]
    public async Task FourReadyDraftsPermitExactCreativeCorrectionAndReplayKeepsOneSourceDocument()
    {
        var f = new Fixture();
        f.SeedModel(1, new(true, [], "Scope supports staffing.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var review = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        for (var i = 0; i < SpecialistAgent.MaximumPitchReviews; i++) f.Add(f.Producer, first.Artifact);
        var issue = new CreativeIssue("accessibility-flash", "controls-accessibility", "blocking", "open",
            "Independent flash and motion controls", "Accessibility wording uses either/or", "game-producer", "pre-production", "State both toggles explicitly");
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false, issue.Change, "")
                { Correction = ProductionCorrection.Create(review.RevisionSha256, [issue]) }));
        f.SeedModel(10, new(true, [], "Both controls are explicit.", Markdown + "\nSeparate flash and reduced-motion controls."));
        var corrected = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(10), f.Context, default);
        var replay = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(10), f.Context, default);
        Assert.Equal("Continue", corrected.Disposition);
        Assert.Equal(corrected.Artifact!.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText());
        Assert.Equal(1, f.Revises); Assert.Equal(1, f.SourceCreates); Assert.Equal(1, f.SourceSubmits);
        Assert.Equal(0, f.PackageCreates); Assert.Empty(f.Todos);
        var current = corrected.Artifact.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        Assert.Equal(review.SourceEvidence, current.SourceEvidence);
        Assert.DoesNotContain("Accepted game pitch", f.Document(current.DocumentId).Revisions.Last().Content);
        Assert.Contains("Exact source references", f.Document(current.DocumentId).Revisions.Last().Content);
        f.Add(f.Producer, corrected.Artifact);
        f.Accept(current);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, current.DocumentId, current.RevisionId, current.RevisionSha256, true, "Accepted exact corrections.", "")));
        var accepted = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(12), f.Context, default);
        Assert.Equal("Completed", accepted.Disposition);
        Assert.Equal(2, f.PackageMembers!.Count);
        Assert.Contains(f.PackageMembers, x => x.RequiredDocumentType == ProductionSources.DocumentType && x.ArtifactId == current.SourceEvidence!.DocumentId);
    }

    [Fact]
    public async Task CompactBriefCannotUnlockStaffingWithoutAcceptedSourceEvidence()
    {
        var f = new Fixture(); f.SeedModel(1, new(true, [], "Ready", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var review = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        f.Add(f.Producer, first.Artifact); f.Accept(review, sources: false);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, true, "Accept", "")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(3), f.Context, default));
        Assert.Equal(0, f.PackageCreates); Assert.Empty(f.Todos);
    }

    [Fact]
    public async Task RepeatedObjectionStopsBeforeAnotherDocumentRevisionAndSurvivesReplay()
    {
        var f = new Fixture();
        f.SeedModel(1, new(false, ["Who approves audio?"], "Need authority.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var previous = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        f.Add(f.Producer, first.Artifact);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, previous.DocumentId, previous.RevisionId, previous.RevisionSha256, false, "Use existing resources.", "")));
        f.SeedModel(3, new(false, ["WHO approves audio ?"], "Still unclear.", Markdown + "\nRepeated demand."));
        for (var replay = 0; replay < 2; replay++)
        {
            var result = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(3), f.Context, default);
            Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition);
            Assert.Contains(previous.RevisionId.ToString(), result.Content);
        }
        Assert.Equal(1, f.Creates);
        Assert.Equal(0, f.Revises);
        Assert.Equal(0, f.Submits);
        Assert.Equal(0, f.PackageCreates);
        Assert.Empty(f.Todos);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyRepeatsAndRotatingObjectionsStopWithoutAModelEvenWhenTurnLimitIsMissing(bool sameQuestion)
    {
        var f = new Fixture();
        var count = sameQuestion ? 2 : SpecialistAgent.MaximumPitchReviews;
        for (var i = 0; i < count; i++)
            f.Add(f.Producer, PitchProtocol.Artifact(PitchProtocol.ReviewType, f.Digest,
                new PitchReview(f.Digest, Guid.NewGuid(), Guid.NewGuid(), "draft", false,
                    [sameQuestion ? "Who approves audio?" : $"What is requirement {i}?"], "Unresolved.")));
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(99), f.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition);
        Assert.Equal(0, f.Creates);
        Assert.Equal(0, f.Revises);
    }

    [Fact]
    public async Task NewOwnerDecisionPermitsBoundedRefinementOfRetainedDraft()
    {
        var f = new Fixture();
        f.SeedModel(1, new(false, ["Who approves audio?"], "Need authority.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var review = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        for (var i = 0; i < SpecialistAgent.MaximumPitchReviews; i++) f.Add(f.Producer, first.Artifact);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false,
                "Owner confirmed existing resources; no new spending.", "") { OwnerDecisionId = Guid.NewGuid() }));
        f.SeedModel(10, new(true, [], "Existing resources cover planning.", Markdown));
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(10), f.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Continue, result.Disposition);
        Assert.True(result.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!.Ready);
        Assert.Equal(0, f.Revises);
        Assert.Equal(1, f.Submits);
        Assert.Equal(0, f.PackageCreates);
    }

    [Fact]
    public async Task ReplayedOwnerDecisionDoesNotResetTheReviewBudget()
    {
        var f = new Fixture();
        var decision = Guid.NewGuid();
        var review = new PitchReview(f.Digest, Guid.NewGuid(), Guid.NewGuid(), "draft", false, ["Who approves audio?"], "Unresolved.");
        var reply = PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false, "Check existing resources.", "") { OwnerDecisionId = decision });
        f.Add(f.Director, reply);
        for (var i = 0; i < SpecialistAgent.MaximumPitchReviews; i++)
            f.Add(f.Producer, PitchProtocol.Artifact(PitchProtocol.ReviewType, f.Digest, review with { Questions = [$"Question {i}?"] }));
        f.Add(f.Director, reply);
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(99), f.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition);
        Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task ExactApprovalOfRetainedReadyBriefSurvivesExhaustedLegacyBudget()
    {
        var f = new Fixture();
        f.SeedModel(1, new(true, [], "Existing scope supports planning.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var review = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        for (var i = 0; i < SpecialistAgent.MaximumPitchReviews; i++) f.Add(f.Producer, first.Artifact);
        f.Accept(review);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, true, "Approved exact brief.", "")));
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(99), f.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Completed, result.Disposition);
        Assert.Equal(1, f.PackageCreates);
        Assert.Single(f.Todos);
    }

    [Fact]
    public async Task UnchangedBriefReusesRevisionAndCanBecomeReadyWithoutADeadline()
    {
        var f = new Fixture();
        f.SeedModel(1, new(false, ["Who may approve audio licensing?"], "Need authority.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(1), f.Context, default);
        var review = first.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        f.Add(f.Producer, first.Artifact, first.Content);
        f.Add(f.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, f.Digest,
            new PitchReply(f.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false,
                "Use supplied audio only. Plan continuously with completion forecasts; no deadline is needed.", "") { OwnerDecisionId = Guid.NewGuid() }));
        f.SeedModel(3, new(true, [], "Existing scope and resources suffice; feasibility refines the forecast.", Markdown));
        var ready = await new SpecialistAgent().HandleCoordinationTurnAsync(f.Request(3), f.Context, default);
        var revised = ready.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        Assert.Equal(review.RevisionId, revised.RevisionId);
        Assert.True(revised.Ready);
        Assert.Equal(0, f.Revises);
        Assert.Equal(1, f.Submits);
        Assert.Equal(0, f.PackageCreates); // Still needs Naomi's exact-revision acceptance.
    }

    internal static string Markdown => string.Join("\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }.Select(x => $"## {x}\nPlanning detail for {x}."));

    [Theory]
    [InlineData(30)]
    [InlineData(12000)]
    public async Task QuestionsThenRefinementThenExactApprovalUnlocksPlanningAndRetriesKeepOneDocument(int outcomeLength)
    {
        var acceptedText = string.Join("\n\n", Enumerable.Range(0, 300).Select(i => $"Approved requirement {i}: deterministic game input."));
        var fixture = new Fixture(new string('x', outcomeLength), acceptedText);
        fixture.SeedModel(1, new(false, ["How many levels are in the MVP?"], "Level count affects workload.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Continue, first.Disposition);
        Assert.Equal(0, fixture.PackageCreates); Assert.Equal(0, fixture.Submits); Assert.Empty(fixture.Todos);
        var repeated = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(first.Artifact!.Payload.GetRawText(), repeated.Artifact!.Payload.GetRawText()); Assert.Equal(1, fixture.Creates);
        var review = first.Artifact.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        fixture.Add(fixture.Producer, first.Artifact, first.Content);
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false, "One level for the MVP.", Markdown + "\nOne level.")));
        fixture.SeedModel(3, new(true, [], "One level, local controls and explicit completion criteria are sufficient to size delivery.", Markdown + "\nOne level."));
        var second = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(3), fixture.Context, default);
        var ready = second.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        Assert.True(ready.Ready); Assert.Equal(1, fixture.Revises); Assert.Equal(1, fixture.Submits); Assert.Equal(0, fixture.PackageCreates);
        Assert.Equal(review.DocumentId, ready.DocumentId); Assert.NotEqual(review.RevisionId, ready.RevisionId);
        fixture.Add(fixture.Producer, second.Artifact, second.Content);
        fixture.Accept(ready);
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, ready.DocumentId, ready.RevisionId, ready.RevisionSha256, true, "This captures the agreed MVP.", "")));
        var completed = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(5), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Completed, completed.Disposition);
        Assert.Equal(1, fixture.PackageCreates); Assert.Contains(fixture.PackageMembers!, x => x.ArtifactId == ready.DocumentId && x.AcceptedRevisionId == ready.RevisionId);
        Assert.Equal("video-game.production-plan.v1", fixture.PackageMembers![0].RequiredDocumentType);
        Assert.Contains(fixture.States.Keys, x => x.Contains("producer"));
        var task = Assert.Single(fixture.Todos);
        Assert.Contains("delivery staffing", task.Title);
        Assert.Equal(fixture.Request(5).WorkContext!.WorkstreamId, task.WorkContext!.WorkstreamId);
        Assert.Equal(fixture.Request(5).WorkContext!.BoardId, task.WorkContext.BoardId);
        Assert.Equal(fixture.Request(5).SessionId, task.WorkContext.CoordinationSessionId);
        Assert.StartsWith("producer-planning:", task.CorrelationId);
        await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(5), fixture.Context, default);
        Assert.Single(fixture.Todos);
        // No board, sprint or metrics capabilities are registered. Initial hiring must not require them.
        var planner = new SpecialistAgent();
        var configured = await planner.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test-model") }))),
            fixture.Context, default);
        Assert.True(configured.Succeeded, configured.Error);
        var legacy = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Id = Guid.NewGuid(), RequesterInstallationId = Guid.Parse(fixture.Context.InstallationId),
            TeamId = task.WorkContext.TeamId, WorkstreamId = task.WorkContext.WorkstreamId, ExpectedTeamRevision = 1,
            Status = "RevisionRequested", DecisionComment = "Before board planning, propose the missing Technical Director and baseline Engineer and QA coverage from the accepted brief.",
            Evidence = [new("scope-capability-gap", ready.RevisionSha256, "Technical feasibility")] };
        var requests = new List<ResourceChangeRequestResponse> { legacy };
        fixture.Runtime.RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
            (_, _) => Task.FromResult(new ResourceChangeReadResponse(requests)));
        fixture.Runtime.RegisterCapability<ResourceChangeProposalRequest, ResourceChangeRequestResponse>(PlatformCapabilities.ResourceChangePropose,
            (r, _) => {
                fixture.StaffingProposals.Add(r);
                var pending = legacy with { Id = Guid.NewGuid(), Status = "Pending", DecisionComment = null,
                    RequesterOrganizationUserId = fixture.Producer, Roles = r.Roles };
                requests.Add(pending);
                return Task.FromResult(pending);
            });
        var assessments = 0;
        fixture.Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (call, _) => {
            assessments++;
            using var input = JsonDocument.Parse(call.GetProperty("messages")[1].GetProperty("text").GetString()!);
            var facts = input.RootElement;
            var roles = facts.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
            Assert.Contains("One level", facts.GetProperty("brief").GetString());
            if (assessments == 1)
            {
                Assert.Equal(legacy.DecisionComment, facts.GetProperty("feedback").GetString());
                Assert.Contains("retired", facts.GetProperty("supersededPolicy").GetString());
            }
            var gaps = facts.GetProperty("Members").GetArrayLength() > 0 ? Array.Empty<SpecialistAgent.CoverageGap>() :
                [new SpecialistAgent.CoverageGap(roles.IndexOf("game-technical-director"), "Resolve technical feasibility before decomposition.")];
            return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant",
                text = JsonSerializer.Serialize(new SpecialistAgent.CoverageAssessment("The first deliverable needs technical feasibility.", gaps)) }));
        });
        await planner.HandlePersonalTodoAsync(task, fixture.Context, default);
        await planner.HandlePersonalTodoAsync(task, fixture.Context, default);
        Assert.Equal(1, assessments);
        var staffing = Assert.Single(fixture.StaffingProposals);
        Assert.Equal("game-technical-director", Assert.Single(staffing.Roles).RoleKey);
        Assert.Equal(0, fixture.MessageSends);
        Assert.Equal(task.WorkContext.WorkstreamId, staffing.WorkstreamId);
        Assert.Contains(staffing.Evidence, x => x.SourceRevision == ready.RevisionSha256);
        fixture.HireTechnicalDirector();
        fixture.Runtime.RegisterCapability<JsonElement, ArtifactPackage>(PlatformCapabilities.ArtifactPackageRead,
            (r, _) => Task.FromResult(new ArtifactPackage(r.GetProperty("packageId").GetGuid(), "Planning", "planning", 1,
                "Accepted", fixture.PackageMembers!, DateTimeOffset.UtcNow)));
        var outcomes = fixture.PackageMembers!.SelectMany(m => {
            var revision = fixture.Document(m.ArtifactId).Revisions.Single(x => x.Id == m.AcceptedRevisionId);
            return GameOutcomeLedger.Read(task.WorkContext.WorkstreamId!.Value, task.WorkContext.TeamId!.Value, m.ArtifactId,
                revision.Id, revision.ContentSha256, m.RequiredDocumentType, revision.Content);
        }).ToArray();
        Assert.True(JsonSerializer.Serialize(outcomes).Length > 128000);
        fixture.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]));
        StartBoardCoordinationRequest? started = null;
        fixture.Runtime.RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard,
            (r, _) => { started = r; return Task.FromResult(JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with {
                Id = Guid.NewGuid(), Status = "Active", Turns = [] }); });
        await planner.HandlePersonalTodoAsync(task, fixture.Context, default);
        Assert.NotNull(started);
        var references = started.Artifact!.Payload.GetProperty("documentReferences").EnumerateArray().ToArray();
        Assert.Equal(fixture.PackageMembers!.Count, references.Length);
        Assert.All(fixture.PackageMembers!, m => Assert.Contains(references, r =>
            r.GetProperty("documentId").GetGuid() == m.ArtifactId && r.GetProperty("revisionId").GetGuid() == m.AcceptedRevisionId));
        Assert.Single(fixture.TeamBoards);
        Assert.Single(fixture.Sprints);
        Assert.Single(fixture.StaffingProposals);
    }

    [Fact]
    public async Task UnmatchedApprovalCannotSkipQuestions()
    {
        var fixture = new Fixture();
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, Guid.NewGuid(), Guid.NewGuid(), "hash", true, "Approved", "")));
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition); Assert.Equal(0, fixture.PackageCreates);
    }

    [Fact]
    public async Task FinalizationCannotManufactureReadiness()
    {
        var fixture = new Fixture();
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1) with { IsFinalization = true }, fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition); Assert.Equal(0, fixture.PackageCreates);
    }

    [Fact]
    public void ReadyFlagWithUnansweredQuestionsIsInsufficient() => Assert.False(PitchProtocol.CanPlan(new(true, ["Unknown scope"], "Ready", Markdown)));

    [Fact]
    public void PitchReviewReadsOneCompleteJsonObjectAndRejectsIncompleteJson()
    {
        var review = new ProducerReview(false, ["Who owns the audio assets?"], "Audio ownership affects scope.", Markdown);
        var json = JsonSerializer.Serialize(review, PitchProtocol.Json);
        Assert.Equal(review.Questions, PitchProtocol.ParseProducerReview(json + "\n<reasoning>extra</reasoning>")!.Questions);
        Assert.Equal(review.Questions, PitchProtocol.ParseProducerReview("```json\n" + json + "\n```")!.Questions);
        Assert.ThrowsAny<JsonException>(() => PitchProtocol.ParseProducerReview(json[..^1]));
    }

    private sealed class Fixture
    {
        public Guid Producer { get; } = Guid.NewGuid(); public Guid Director { get; } = Guid.NewGuid();
        public string Digest => "pitch-digest";
        private Guid Session { get; } = Guid.NewGuid(); private Guid Workstream { get; } = Guid.NewGuid(); private Guid Team { get; } = Guid.NewGuid();
        private List<AgentCoordinationTurn> Transcript { get; } = [];
        private Dictionary<Guid, ArtifactDocument> Documents { get; } = [];
        public ArtifactDocument Document(Guid id) => Documents[id];
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public AgentRuntimeContext Context { get; }
        public int Creates, Revises, Submits, PackageCreates, MessageSends, SourceCreates, SourceSubmits;
        public List<PersonalTodoItem> Todos { get; } = [];
        public List<ResourceChangeProposalRequest> StaffingProposals { get; } = [];
        private Guid? Board => null;
        public AgentTestRuntime Runtime { get; private set; } = null!;
        private bool TechnicalDirectorHired;
        private Guid TechnicalDirector { get; } = Guid.NewGuid();
        public List<WorkBoardSummary> TeamBoards { get; } = [];
        public List<WorkSprint> Sprints { get; } = [];
        public void HireTechnicalDirector()
        {
            TechnicalDirectorHired = true;
            Runtime.RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>(TeamBoards.ToList()));
            Runtime.RegisterCapability<CreateWorkBoardRequest, WorkBoardSummary>(WorkBoardCapabilities.Create, (r, _) => {
                Assert.Equal(Team, r.TeamId); Assert.Equal(Workstream, r.WorkstreamId);
                Assert.InRange(r.Description!.Length, 1, 2048);
                Assert.Contains(Workstream.ToString("D"), r.Description);
                Assert.Matches("^[A-Z][A-Z0-9]{1,11}$", r.Key!);
                var board = new WorkBoardSummary(Guid.NewGuid(), r.Name, r.Description!, false, false, 1, []) {
                    WorkstreamId = Workstream, TeamId = Team };
                TeamBoards.Add(board); return Task.FromResult(board);
            });
            Runtime.RegisterCapability<ConfigureProfileOrchestrationRequest, JsonElement>(WorkOrchestrationCapabilities.ConfigureProfile,
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {})));
            Runtime.RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>(Sprints));
            Runtime.RegisterCapability<CreateWorkSprintRequest, WorkSprint>(WorkSprintCapabilities.Create, (r, _) => {
                var sprint = new WorkSprint(Guid.NewGuid(), r.BoardId, r.Name, r.Goal!, "Planned", r.StartsAt, r.EndsAt,
                    null, null, null, 0, 0, 0, 0, 1);
                Sprints.Add(sprint); return Task.FromResult(sprint);
            });
            Runtime.RegisterCapability<JsonElement, ArtifactPackage>(PlatformCapabilities.ArtifactPackageRead,
                (r, _) => Task.FromResult(new ArtifactPackage(r.GetProperty("packageId").GetGuid(), "Planning", "planning", 1,
                    "Submitted", PackageMembers!, null)));
        }
        public IReadOnlyList<ArtifactPackageMember>? PackageMembers;
        public Fixture(string outcome = "Ship the accepted game", string acceptedText = "Accepted game pitch")
        {
            var document = Guid.NewGuid(); var revision = Guid.NewGuid();
            var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(acceptedText)));
            Documents[document] = new(document, "Pitch", "video-game.vision.v1", "Approved", revision, null, revision, null, null,
                [new(revision, 1, null, acceptedText, digest, "Accepted", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)]);
            var vision = new GameVisionBrief(Digest, "outcome", "loop", "platform", "art", "MVP", [], "criteria", [])
                { HighLevelGddArtifactId = document, HighLevelGddAcceptedRevisionId = revision, HighLevelGddRevisionSha256 = digest };
            Add(Director, PitchProtocol.Artifact(PitchProtocol.BriefType, Digest, new PitchBrief(vision, document, revision, digest)));
            var runtime = Runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.TeamRosterReadV2,
                    (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { team = new { teamId = Team.ToString(), revision = TechnicalDirectorHired ? 2 : 1, members = TechnicalDirectorHired ? new object[] { new { employeeId = TechnicalDirector.ToString(), agentInstallationId = Guid.NewGuid(), isAvailable = true, runtimeEligibility = "Eligible", declaredRoleKeys = new[] { "game-technical-director" } } } : Array.Empty<object>() } })))
                .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                    (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
                .RegisterCapability<ResourceChangeProposalRequest, JsonElement>(PlatformCapabilities.ResourceChangePropose,
                    (request, _) => { StaffingProposals.Add(request); return Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid() })); })
                .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1",
                    (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { succeeded = true, chat = new { id = Guid.NewGuid(), participants = new[] { new { organizationUserId = Director, employeeType = "Agent", displayName = "Director", role = "Member" } } } })))
                .RegisterCapability<JsonElement, JsonElement>("communication.message.send.v1",
                    (request, _) => {
                        MessageSends++;
                        return Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), chatId = request.GetProperty("chatId").GetGuid(), chatTurnId = Guid.NewGuid() }));
                    })
                .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(WorkstreamCapabilityNames.ReadV1,
                    (request, _) => Task.FromResult(new WorkstreamDetail(Workstream, "Game", outcome, [], "concept", "Active",
                        Producer, null, null, null, "video-game-production.v2", 4, null, "profile-digest", 1)))
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                    (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Producer, "Producer", Director, "Director", 1, Todos)], Producer)))
                .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (request, _) => {
                    var todo = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), Producer, Producer, "Producer", request.Title,
                        request.Description!, PersonalTodoStatuses.Ready, request.Priority, 1, 1, null, null, null, [], null, null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = request.CorrelationId, WorkContext = request.WorkContext };
                    Todos.Add(todo); return Task.FromResult(todo);
                })
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead,
                    (request, _) => Task.FromResult(Documents[request.GetProperty("artifactId").GetGuid()]))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(request.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (request, _) => { Assert.InRange(request.Payload.GetRawText().Length, 1, 128000); var saved = State(request.StateKey, request.Payload, (request.ExpectedRevision ?? 0) + 1); States[request.StateKey] = saved; return Task.FromResult(saved); })
                .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate,
                    (request, _) => { if (request.DocumentType == ProductionSources.DocumentType) SourceCreates++; else Creates++; var id = Guid.NewGuid(); var rev = new ArtifactRevision(Guid.NewGuid(), 1, null, request.Content, "draft1", "Draft", DateTimeOffset.UtcNow, null, null);
                        var doc = new ArtifactDocument(id, request.Title, request.DocumentType, "Draft", rev.Id, null, null, null, null, [rev]) { WorkstreamId = Workstream, TeamId = Team };
                        Documents[id] = doc; return Task.FromResult(doc); })
                .RegisterCapability<CreateArtifactRevision, ArtifactRevision>(PlatformCapabilities.ArtifactRevise,
                    (request, _) => { Revises++; var doc = Documents[request.ArtifactId]; Assert.Equal(doc.LatestRevisionId, request.ExpectedBaseRevisionId);
                        var rev = new ArtifactRevision(Guid.NewGuid(), 2, request.ExpectedBaseRevisionId, request.Content, "draft2", "Draft", DateTimeOffset.UtcNow, null, null);
                        Documents[doc.Id] = doc with { LatestRevisionId = rev.Id, Revisions = [.. doc.Revisions, rev] }; return Task.FromResult(rev); })
                .RegisterCapability<SubmitArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactSubmit,
                    (request, _) => { var doc = Documents[request.ArtifactId]; if (doc.DocumentType == ProductionSources.DocumentType) SourceSubmits++; else Submits++; Documents[doc.Id] = doc with { Revisions = doc.Revisions.Select(x => x.Id == request.RevisionId ? x with { Status = "Submitted" } : x).ToList() }; return Task.FromResult(Documents[doc.Id]); })
                .RegisterCapability<CreateArtifactPackage, ArtifactPackage>(PlatformCapabilities.ArtifactPackageCreate,
                    (request, _) => { PackageCreates++; PackageMembers = request.Members; return Task.FromResult(new ArtifactPackage(Guid.NewGuid(), request.Name, request.PackageType, 1, "Draft", request.Members, null)); })
                .RegisterCapability<JsonElement, ArtifactPackage>(PlatformCapabilities.ArtifactPackageSubmit,
                    (request, _) => Task.FromResult(new ArtifactPackage(request.GetProperty("packageId").GetGuid(), "Planning", "planning", 1, "Submitted", PackageMembers!, null)));
            Context = runtime.CreateContext(identity: new AgentIdentity(Producer.ToString(), "Producer", null, "Producer", null, [], null, Director.ToString(), "Creative Director"));
        }
        public void Add(Guid speaker, AgentCoordinationArtifactSubmission artifact, string content = "Clarification") => Transcript.Add(new(Guid.NewGuid(), Transcript.Count,
            speaker, "Continue", content, DateTimeOffset.UtcNow, new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "transport-digest")));
        public AgentCoordinationTurnRequest Request(int turn) => new(Session, turn, turn, "Pitch", "Refine pitch", [],
            new(Producer, Guid.NewGuid(), "Producer", "Producer"), new(Director, Guid.NewGuid(), "Director", "Director"), false, Transcript.ToList())
            { WorkContext = new(Guid.NewGuid(), Workstream, Team, Board, null, null, null, Guid.NewGuid(), null, null) };
        public void SeedModel(int turn, ProducerReview review) { var key = $"pitch-producer:{Session:N}:{turn}"; States[key] = State(key, JsonSerializer.SerializeToElement(review), 1); }
        public void Accept(PitchReview review, bool sources = true) {
            foreach (var reference in review.DocumentReferences.Where(x => sources || x.DocumentId == review.DocumentId)) {
                var doc = Documents[reference.DocumentId]; Documents[doc.Id] = doc with { AcceptedRevisionId = reference.RevisionId,
                    Revisions = doc.Revisions.Select(x => x.Id == reference.RevisionId ? x with { Status = "Accepted" } : x).ToList() };
            }
        }
        private static AgentOperatingStateResponse State(string key, JsonElement payload, long revision) => new(Guid.NewGuid(), key,
            "test", 1, "Active", new Dictionary<string, string>(), [], key, [], Guid.NewGuid(), payload, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}

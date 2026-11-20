using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using TicketConversations;

namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    private sealed record QuestionThread(Guid ItemId, long PlanningRevision, string Route, Guid RecipientInstallationId);
    private sealed record PlanningQuestions(IReadOnlyList<QuestionThread> Threads);

    private static string PlanningQuestionsKey(Guid boardId) => $"planning-ticket-questions:{boardId:N}";

    private static async Task<WorkItemComment> AskPlanningQuestionAsync(ReviewAction action, ReviewFact source,
        GameProductionPlanningCycleV1 cycle, AgentTeamContext roster, AgentRuntimeContext context, CancellationToken token)
    {
        var ticket = await context.Platform.Work.ReadItemAsync(new(cycle.BoardId, source.Id), token);
        if (ticket.PlanningRevision.ToString() != source.Revision)
            throw new PlanningDependencyPendingException("The ticket changed during review. Gabriel will reassess its current scope.");

        AgentTeammate? recipient;
        if (action.Kind == "clarify-scope")
            recipient = SelectRoleMember(roster, VideoGameRoleKeys.TechnicalDirector);
        else
        {
            var authors = ticket.StageAssignments.Where(s => s.StageKey is "development" or "specialist-execution").ToArray();
            var candidates = ConsultationRouting.Candidates(roster.Members, context.Identity!.EmployeeId);
            var assigned = candidates.Where(m => authors.Length == 0
                ? RoleTaxonomy.SatisfiesRole(m.DeclaredRoleKeys, VideoGameRoleKeys.Engineer) &&
                    Matches(m, ticket.AssignedEmployeeId, ticket.AssignedInstallationId)
                : authors.Any(s => Matches(m, s.OrganizationUserId, s.AgentInstallationId))).ToArray();
            recipient = assigned.Length == 1 ? assigned[0] : null;
        }
        if (recipient?.AgentInstallationId is not Guid installation)
            throw new PlanningDependencyPendingException(action.Kind == "clarify-scope"
                ? "Gabriel needs an available Technical Director to resolve the ticket scope."
                : "Gabriel needs an available, unambiguous implementation owner for this ticket.");

        var route = AgentOperatingStateKey.Create("planning-question", new { ticket.Id, ticket.PlanningRevision, action.Kind, installation });
        // Register where to read before posting. A crash after the comment commits cannot orphan its reply.
        var watch = new QuestionThread(ticket.Id, ticket.PlanningRevision, route, installation);
        await new RevisionSafeProjectState(context.Platform).MergeAsync<PlanningQuestions>(PlanningQuestionsKey(cycle.BoardId),
            "planning.ticket-questions.v1", 1, prior => new((prior?.Threads ?? []).Append(watch).DistinctBy(q => q.Route).ToArray()),
            new Dictionary<string, string>(), route + ":watch", token);
        var thread = await Discussion.ReadAsync(cycle.BoardId, ticket.Id, context, token);
        var previous = thread.Where(c => IsPlanningQuestion(c, route, context))
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToArray();
        var body = $"@{recipient.DisplayName}: {action.Rationale.Trim()}";
        var comment = previous.FirstOrDefault(q => q.Body == body || PlanningReply(q, installation, thread) is null);
        if (comment is null)
        {
            // Concurrent reviews share one open question per ticket revision, purpose and owner.
            var key = AgentOperatingStateKey.Create("planning-question-send", new { route, after = previous.FirstOrDefault()?.Id });
            comment = await context.Platform.Work.CommentAsync(new(cycle.BoardId, ticket.Id, body, key)
                { Kind = "discussion.request", CausationId = route }, token);
        }
        return comment;
    }

    private static bool Matches(AgentTeammate member, Guid? employee, Guid? installation) =>
        (employee.HasValue || installation.HasValue) && (!employee.HasValue || employee.ToString() == member.EmployeeId) &&
        (!installation.HasValue || installation == member.AgentInstallationId);

    private static WorkItemComment? PlanningReply(WorkItemComment question, Guid installation, IEnumerable<WorkItemComment> comments) =>
        comments.FirstOrDefault(c => c.Kind == "discussion.reply" && c.AuthorKind == "AgentInstallation" &&
            c.AuthorSubjectId == installation && c.CausationId == Discussion.Correlation(question.Id, question.Revision));

    private static bool IsPlanningQuestion(WorkItemComment comment, string route, AgentRuntimeContext context) =>
        comment.Kind == "discussion.request" && comment.CausationId == route && comment.AuthorKind == "AgentInstallation" &&
        comment.AuthorSubjectId.ToString() == context.InstallationId;

    private static async Task<IReadOnlyList<ReviewFact>> ReadPlanningAnswersAsync(GameProductionPlanningCycleV1 cycle,
        WorkBoardDetail board, AgentRuntimeContext context, CancellationToken token)
    {
        var watches = await context.Platform.ReadOperatingStateAsync<PlanningQuestions>(PlanningQuestionsKey(cycle.BoardId), token);
        var facts = new List<ReviewFact>();
        foreach (var group in (watches?.Payload.Threads ?? []).GroupBy(q => q.ItemId))
        {
            var ticket = board.Items.FirstOrDefault(i => i.Id == group.Key);
            if (ticket is null || group.All(q => q.PlanningRevision != ticket.PlanningRevision)) continue;
            var comments = await Discussion.ReadAsync(cycle.BoardId, ticket.Id, context, token);
            foreach (var watch in group.Where(q => q.PlanningRevision == ticket.PlanningRevision))
            foreach (var question in comments.Where(c => IsPlanningQuestion(c, watch.Route, context)))
            {
                var reply = PlanningReply(question, watch.RecipientInstallationId, comments);
                facts.Add(new("planning-question", question.Id, question.Revision.ToString(), $"Ticket {ticket.Id}: {question.Body}")
                    { AwaitingResult = reply is null });
                if (reply is not null)
                    facts.Add(new("planning-answer", reply.Id, reply.Revision.ToString(), $"Ticket {ticket.Id}, {reply.AuthorDisplayName}: {reply.Body}"));
            }
        }
        return facts;
    }
}

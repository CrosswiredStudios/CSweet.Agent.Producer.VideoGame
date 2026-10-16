using System.Text.Json;

namespace TicketConversations;

internal static class TaskRevisionComments
{
    internal const string RequestMarker = "[task-revision-request:v1]";
    internal const string ContinueMarker = "[task-revision:v1:continue]";
    internal const string ReplaceMarker = "[task-revision:v1:replace]";
    internal const string ClarifyMarker = "[task-revision:v1:clarify]";
    internal const string Instructions = """
        This is a task revision request. Review observed evidence against current ticket scope and discussion.
        Return ONLY JSON with decision (continue, replace, or clarify), supersedes, context, pseudocode,
        assumptions, verification and reason (all strings). Use continue only for a concrete correction to
        implementation guidance within unchanged deliverable, accepted requirements and criteria. Name the
        exact original instruction or earlier comment being superseded. Supply actionable file/type/member
        context, pseudocode, assumptions to recheck, and focused verification; distinguish proposed symbols
        from inspected facts. Corrected implementation guidance in this comment takes precedence over the
        identified original description. Never waive review, tests, accepted outcomes, security or authority.
        Use clarify if evidence is insufficient; reason must state the missing evidence or specific question.
        Use replace when the deliverable, acceptance criteria, dependencies or task size changes substantially.
        For replace, context describes small independently deliverable replacement tickets and their links;
        reason explains why to close the original as superseded/cancelled, never completed. The Producer must
        preserve history and valid work, redirect downstream dependencies and refresh readiness before starting
        replacement work. Do not claim tickets were created or closed. A recommendation grants no execution.
        A task with two independently deliverable outputs must be split, even if they are sequential.
        Keep the combined fields under 5500 characters. Do not put protocol markers in any field.
        """;

    internal sealed record Reply(string Decision, string Supersedes, string Context, string Pseudocode,
        string Assumptions, string Verification, string Reason);

    internal static string Format(string json)
    {
        var reply = JsonSerializer.Deserialize<Reply>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Task revision reply is empty.");
        var fields = new[] { reply.Supersedes, reply.Context, reply.Pseudocode, reply.Assumptions, reply.Verification, reply.Reason };
        if (reply.Decision is not ("continue" or "replace" or "clarify") || string.IsNullOrWhiteSpace(reply.Reason) ||
            fields.Sum(x => x?.Length ?? 0) > 5500 || fields.Any(x => x?.Contains("[task-revision:", StringComparison.Ordinal) == true) ||
            reply.Decision == "continue" && fields.Any(string.IsNullOrWhiteSpace) ||
            reply.Decision == "replace" && string.IsNullOrWhiteSpace(reply.Context))
            throw new InvalidOperationException("Task revision needs a valid decision and concrete correction or replacement evidence.");
        var marker = reply.Decision switch { "continue" => ContinueMarker, "replace" => ReplaceMarker, _ => ClarifyMarker };
        return $"{marker}\n{reply.Reason}\n\nSupersedes: {reply.Supersedes}\n\nContext: {reply.Context}\n\n" +
            $"Pseudocode: {reply.Pseudocode}\n\nAssumptions: {reply.Assumptions}\n\nVerification: {reply.Verification}";
    }

    internal static bool HasDecision(string body, string marker) =>
        body.Replace("\r", "").Split('\n').Count(line => line.StartsWith("[task-revision:", StringComparison.Ordinal)) == 1 &&
        body.Replace("\r", "").Split('\n').Contains(marker, StringComparer.Ordinal);
}

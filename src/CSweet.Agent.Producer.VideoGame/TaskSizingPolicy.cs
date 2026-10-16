namespace CSweet.Agent.Producer.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const string TaskSizingDirection = "Split every task that can yield two independently deliverable, verifiable changes, even when one depends on the other. " +
        "Keep validation of a change with that change. Never bundle deliverables to fit a preferred ticket count. " +
        "The Technical Director owns implementation guidance in each engineering description: Outcome, Context (paths/types/members to update, verified or proposed), " +
        "Pseudocode (ordered data flow and failure handling), Assumptions (dependencies and invalidating evidence), and Verification (checks and expected results). " +
        "Use current ticket comments for explicit corrections that supersede outdated implementation guidance within accepted scope. " +
        "For a substantially different deliverable, close the old ticket as superseded/cancelled, never completed; create small linked replacements, " +
        "retain history and valid work, redirect downstream dependencies, and refresh estimates and readiness before execution. ";
}

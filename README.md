# Video Game Producer

Leads project delivery, boards, sprints, schedules, dependencies, staffing evidence, risks, and status reporting. The Producer manages delivery but does not make creative, technical-feasibility, QA, spending, or hiring decisions for their respective authorities.

## Kanban model

The Producer deliberately uses two boards without duplicating work:

- The team board is the authoritative record for project deliverables. Milestone epics and feature/content stories are planning containers; tasks, bugs, research spikes, and creative reviews are executable leaf tickets. Only executable leaves can enter a sprint or delivery metrics.
- The Producer's personal board contains only management commitments such as handoff validation, planning reconciliation, estimation, sprint readiness, blocker follow-up, gate evidence, reporting, and capacity proposals. Each commitment carries source context linking it to the authoritative workstream, board, ticket, sprint, gate, decision, or coordination session.
- Specialist assignments remain team-board work and are never copied into personal todos, preventing shadow tickets and double-counted throughput.

The five-minute attention cycle reconciles authoritative state and queues or requeues durable personal commitments. A serialized personal-todo handler performs the longer workflows with at most one Producer commitment running at a time.

## Planning and staffing gates

- An accepted, digest-verified Creative Director handoff is sufficient for the Producer to configure the board and publish milestone shells.
- Detailed backlog publication requires a Technical Director and accepted creative brief. An available Game Designer contributes design proposals; a dedicated designer is not mandatory.
- QA readiness evidence is required before executable work moves to `Ready`, but QA does not block initial backlog drafting.
- A runnable prototype or vertical-slice sprint additionally requires a Game Engineer.
- Every sprint requires exact eligible installations for every accountable role used by its selected executable leaves; milestone and release gates require the reviewers declared by the pinned profile.

Ticket assignment uses exact roles as hard boundaries, required skills and capabilities as hard constraints, and preferred-skill coverage plus current WIP only for deterministic ranking. Historical individual velocity is never used to select or rank people. `video-game-development` remains a compatibility umbrella and is never sufficient as the only ticket-level skill match.

## Contract

- Package ID: `com.csweet.video-game-producer`
- Version: `2.3.4`
- Provides: `work.execution.run.v1`
- Activation: always on, with five-minute attention reviews
- Requested platform/provider capabilities: typed planning, work management, governed staffing, communication, artifacts and brokered model access (see manifest).
- Event subscriptions: attention, coordination, workstream, workforce, hiring, work-item, sprint, and management changes
- Network access: none

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.Producer.VideoGame -- --self-test
```

The tests run entirely in memory and require no C-Sweet instance or credentials.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.31.1 and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Progressive delivery (2.3.4)

The Producer drafts a sprint before implementation staffing is complete, proposes a technical lead when
needed, and requests further roles only for uncovered backlog responsibilities. Approved unfulfilled slots
are not proposed again. Drafts have provisional dates, at most eight tentative tickets, and no committed capacity. Tickets can be unassigned; missing coverage stays visible.
The technical plan is bound to the accepted brief; a roster change rebinds existing unassigned tickets
instead of regenerating the backlog. Missing roles and their transitive dependencies remain in Backlog;
independent covered work proceeds through estimation. Only specialist-estimated, dependency-consistent,
QA-reviewed scope is committed for execution; tentative tickets outside that scope return to the backlog. Unresolved authority questions are escalated to the Creative Director
and require an updated accepted brief before commitment. Periodic review and workforce/work-item events
resume useful work. Dedicated specialist roles remain capability boundaries; there is no implicit role aliasing.
Communication chat read/create/send permissions support those scoped conversations and staffing proposals.

Roster reads respect the platform limit of 100 entries per page. Bootstrap handoff requires
the host roster fix allowing the active team lead's direct manager to inspect the team before
Workstream creation, and including provided work capabilities in roster eligibility.

## Pitch refinement before staffing

The Creative Director supplies exact accepted pitch and GDD references. The Producer reads their
contents and iterates a single shared production-brief document, asking focused questions while
the Creative Director contributes answers and revised wording. The accepted pitch remains the
scope authority; the working brief records delivery detail, assumptions and unresolved questions.
Each turn and document revision is durable. Model decisions are cached per session/turn before
document writes so retries retain the same decision. No staffing handoff is recorded until the
Producer reports justified confidence with zero planning questions and the Creative Director
accepts that exact revision. A stalled or bounded conversation never counts as readiness.
The accepted brief includes immutable exact pitch/GDD source appendices and is packaged for technical planning.
New coordination payloads are pitch-brief.v1, pitch-review.v1 and pitch-reply.v1 under
video-game.production. Existing completed staffing decisions are not silently revoked.
Reimport both agents to enable this protocol; legacy unrefined handoffs are blocked.

## Shared collaboration SDK

Uses CSweet.Agent.SDK 3.31.1 typed document references, explicit coordination read sharing,
accepted-revision lookup, and handoff readiness checks. Pitch content and staffing judgment
remain agent-specific. See the SDK's `docs/collaboration.md` for reusable documentation requests,
clarification, review, and personal-work dependency waits. The matching C-Sweet host is required
for sharing at every coordination start; package import alone does not update the host.

## Provider queue handling

Uses SDK 3.31.1 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Hiring kickoff

Onboarding contacts the authoritative Creative Director, introduces the Producer in the owner's
conversation, and persists the kickoff before acknowledging the lifecycle event. Stable message
keys and durable state prevent duplicate introductions on retries. Project setup must be ready
before the existing exact-document pitch refinement workflow begins. That workflow retains the
shared brief and accepted handoff before the Producer proposes workload-backed staffing.

### Immediate work after brief acceptance (2.3.4)

Accepting the exact collaborative production brief now creates the Producer's linked scope,
technical-backlog, and staffing task before the coordination session completes. The accepted
brief remains in durable operating state, and later attention reviews reuse the same task.
Approval replays do not create another task; unresolved questions never unlock staffing work.
Board creation also uses a valid 2-12 character alphanumeric key.

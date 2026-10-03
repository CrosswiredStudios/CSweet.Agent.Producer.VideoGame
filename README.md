# Video Game Producer

Leads project delivery, boards, sprints, schedules, dependencies, staffing evidence, risks, and status reporting. The Producer follows the actual reporting manager. A short manager brief can start a project proposal and team plan without a Creative Director, pitch, or GDD. The Producer manages delivery but does not make spending or hiring decisions for their respective authorities.


## Direct manager kickoff (2.9.0)

The Producer now answers direct Communications turns and finishes each valid turn with a final response. An explicit manager request can generate a lightweight project proposal and a role-specific team proposal through the existing approval paths. Project and hiring approvals remain visible to the manager; a submitted proposal is not an approved project, team, or hire. The Producer package provides its own lightweight game project profile, so project setup does not depend on importing the Creative Director package. The project and team proposals are separate, so an approved project may still need its approved team attached before board execution. The Producer does not require a Creative Director, accepted pitch, or GDD for this path. The older formal creative-handoff flow still applies when that workflow is used.
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
- Version: `2.15.4`
- Project planning questions go to the Creative Director through delegated `work-planning` decisions. Recorded manager direction wakes a new specialist planning pass; only a material escalation reaches the CEO.
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

The installation settings **Maximum context-window tokens** (`maxContextWindowTokens`, default
220,000) and **Maximum output tokens** (`maxOutputTokens`, default 32,000) configure the Producer's
planning ceiling and per-response output budget for pitch refinement, delivery review, and
specialist work. The output budget includes model reasoning and must remain below the context
ceiling. The agent imposes no fixed maximum, so both values can match the selected model. These
settings do not enlarge the model's actual context window or override the provider profile's
authoritative execution ceiling.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.58.0 and `CSweet.WorkManagement.Contracts` 3.24.0, plus the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Progressive delivery (2.3.5)

The Producer proposes a baseline Technical Director, game engineer, and QA specialist from the
exact accepted brief before detailed decomposition. It drafts a sprint while hiring is incomplete and
requests further roles only for uncovered backlog responsibilities. Approved unfulfilled slots
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

Uses CSweet.Agent.SDK 3.40.0 typed document references, explicit coordination read sharing,
accepted-revision lookup, and handoff readiness checks. Pitch content and staffing judgment
remain agent-specific. See the SDK's `docs/collaboration.md` for reusable documentation requests,
clarification, review, and personal-work dependency waits. The matching C-Sweet host is required
for sharing at every coordination start; package import alone does not update the host.

## Provider queue handling

Uses SDK 3.40.0 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Hiring kickoff

Onboarding contacts the authoritative Creative Director, introduces the Producer in the owner's
conversation, and persists the kickoff before acknowledging the lifecycle event. Stable message
keys and durable state prevent duplicate introductions on retries. Project setup must be ready
before the existing exact-document pitch refinement workflow begins. That workflow retains the
shared brief and accepted handoff before the Producer proposes workload-backed staffing.

### Immediate work after brief acceptance (2.3.5)

Accepting the exact collaborative production brief now creates the Producer's linked scope,
technical-backlog, and staffing task before the coordination session completes. The accepted
brief remains in durable operating state, and later attention reviews reuse the same task.
Approval replays do not create another task; unresolved questions never unlock staffing work.
Board creation also uses a valid 2-12 character alphanumeric key.

### Documentation and hiring precede the team board (2.3.5)

The Producer requests the Director's documentation, coauthors a scoped production brief,
and saves the exact accepted understanding. A personal task proposes one justified Technical
Director without requiring a team board or historical flow metrics. The Director reviews
that initial proposal against the accepted brief. Only after the Technical Director is
available does the Producer create/configure the team board and begin sprint/backlog planning.
The regression follows questions, document refinement, acceptance, a hiring proposal with no
board APIs available, and then board/sprint creation after technical leadership is hired.

### Accepted planning recovery (2.5.0)

Attention reviews discover workstreams from durable accepted handoffs as well as formal supervision assignments. The host filters reads against current team or management visibility. A Producer hired as a team member therefore continues planning after brief acceptance and subsequent staffing changes without being assigned Director approval authority.

### Multi-stage staffing (2.5.0)
Producer staffing binds each missing canonical delegation independently and preserves existing execution, review, QA and platform assignments. Estimates and implementation capacity use the specialist-execution owner explicitly. Candidate selection waits for all declared delegation stages to be staffed. Missing review or QA roles feed the normal staffing proposal path. Stage definitions and delegation recommendations must still be supplied by technical planning.

Delivery acceptance reviews owned active-sprint Producer gates from exact worker output and document revision content, or matching technical/independent QA/merge evidence. Reviews cover every ticket acceptance criterion and are saved before idempotent approval submission. Missing evidence leaves a diagnostic comment and the approval pending. Requires work.orchestration.approval.decide; it grants no CEO hiring/spending authority.

## Release notes

See [versioned release notes](releases/README.md). Add the matching note with every agent version change.


### 2.5.1 team board access

Declare work.item.read and work.item.comment at team scope so approved team onboarding grants can materialize the access needed by board planning and delivery reconciliation. Organization-scoped declarations did not produce these team grants, causing board reads to fail after workflow configuration. No organization-wide board access is added.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.

Producer-owned ticket estimates use a board conversation with QA. QA invites the Producer to author estimates for the exact requested scope, checks the returned coverage, and records its review. Estimate provenance identifies the Producer's original artifact; this review does not replace the separate QA sprint-readiness assessment.

Unresolved planning questions are recorded as durable workstream decisions with exact accepted-brief evidence. Attention reviews restore missing decision records even when the planning ToDo is already blocked. Stable keys prevent duplicate requests; decisions do not automatically rewrite the accepted brief or approve affected scope.

## Manager-directed delivery

An approved short brief can use profile `video-game-manager-brief.v1` version 2. Gabriel discovers
his accountable projects, attaches already approved team members, obtains a bounded technical plan
from the Technical Director or Software Architect, publishes the tickets and sprints, assigns a
Software Developer and technical reviewer, and starts each increment after authoritative preflight.
The workflow requires real published test results, independent review, Producer acceptance and a
governed exact-candidate merge. A dedicated QA employee and formal creative handoff are optional.

The personal delivery commitment survives restarts. Hiring, planning, repository and sprint events
wake reconciliation; its durable wait provides bounded missed-event recovery. Existing version-1
projects retain their immutable profile until its governed upgrade is approved. Project/hiring,
repository and final completion decisions still respect the business's approval policies.

Delivery setup requests `work.project-delivery.prepare.v1`; repository-bound ticket assignment uses
`work.item.delivery.finalize`. Profile upgrades and final lifecycle changes use
`platform.workstream.change.propose.v1`. Source provisioning and team repository discovery retain
platform policy checks, and `git.merge.review.v2` / `git.merge.authorize.v2` are limited to assigned
work-item evidence and acceptance. These declarations require effective approved installation grants.

The Producer uses its existing work.orchestration.retry capability for one bounded retry per stage and missing-section error. It retains the current assignment revision, host attempt limit, and substantive acceptance gates. Other blockers are not automatically retried.

## Correcting mixed planning and implementation work

The Technical Director plans and reviews. Engineering writes code, builds prototypes, implements performance changes, and commits repository work. QA independently validates execution. The Producer reviews blocked Technical Director assignments and completed documents against the exact accepted criteria. An incompatible assignment creates a durable role-replanning commitment instead of approving incomplete delivery or repeatedly retrying it.

The Producer requests `work.orchestration.cancel` at team scope for this recovery. It remains subject to the host's board-manager authority and grants. Cancellation occurs only after a corrected technical proposal preserves original requirements, exact acceptance criteria, constraints, ticket identities, unrelated work, and implementation dependencies. Active work is allowed to finish first. The existing `work.sprint.carryover` capability then moves unfinished work into a planned replacement sprint; completed work and previous execution attempts stay in their original history. Specialist estimates, delivery finalization, QA readiness, and sprint preflight still apply. A changed or invalid scope blocks recovery before cancellation.

Version 2.10.1 stores each original ticket snapshot separately and writes a compact header only after all pages are durable. This retains the complete accepted scope while respecting the host's 65,536-character operating-state limit. Interrupted writes reuse the persisted pages; replanning hydrates the exact original snapshots before validating coverage and freshness.

Version 2.10.2 gives a failed role-replanning proposal one durable correction session with exact scope discrepancies. The Producer reuses that session on reconnect and still requires the full coverage check before replacing a sprint. Terminal correction failures remain blocked for diagnosis.

Version 2.10.3 carries the original role-repair constraints into every resulting ticket. All old mixed-ticket completion criteria stay with engineering/QA delivery; the retained Technical Director ticket receives separate plan criteria. This revised policy has one stable correction session per source session, with prior attempts preserved.

Version 2.10.4 sends a structured `roleRepair` extension on the existing planning-cycle artifact, pinning the source ticket ID and SHA-256 of its canonical planning. Technical Director 2.11.6 or later preserves that source scope directly and produces linked planning, engineering and independent QA work. The `structured-v1` correction identity is durable and bounded; retries recover the same session, including terminal failure. Accepted document references and the full Producer coverage gate remain intact. Update Technical Director before resuming the Producer's blocked role-replanning commitment. No new capability grants are required.
### Manager-directed delivery recovery

After resolving an infrastructure blocker, a manager in the Producer's active reporting chain can send
`Retry ticket VGDEMO-22: The workspace prerequisite has been repaired.` in the Producer chat.
Use the actual ticket identifier. The Producer re-reads its current owned boards and active
execution, then requests the exact blocked stage through its existing `work.orchestration.retry`
authority. The host still enforces ownership, assignment revision and remaining attempts.
A repeated delivery of the same message uses the same retry key. Colleague messages, quoted
commands, cancelled execution, stale stages and exhausted budgets cannot trigger this action.
This explicit request does not relax acceptance criteria or automatically retry substantive blockers.

When a repaired ticket has exhausted its stage attempt limit, use
'Replan ticket VGDEMO-22: Describe the repaired infrastructure condition.'
from the Producer's reporting chain. The Producer records a durable scope snapshot and
recovery commitment, preserves the old execution and completed work, and carries unfinished
tickets to one planned sprint. Existing estimates/readiness/preflight workflows must authorize
its start. Recovery refuses to interrupt running work or pending approvals, and never resets
attempt counts or claims acceptance.

## Owner-directed scope amendments (2.11.0)

`Amend ticket IDENTIFIER: direction` accepts an explicit current-message correction from the Producer's authenticated reporting chain. It discovers one current execution on a board the Producer manages, requires a stopped review boundary and no concurrent work or other pending approval, and records the authorizing turn plus bounded exact before/after replacements. Historical chat, retrieved content and colleague messages cannot authorize this action. General setup/staffing authority is unchanged.

The Producer obtains a corrected Technical Director proposal, checks it against the exact amendment while preserving unrelated criteria, ticket hierarchy, dependencies, roles and completed work, and then uses the normal cancellation/carryover workflow. Original attempts and source publications remain in history. Revised delivery briefs are finalized from the new canonical planning so coding and QA do not receive stale criteria. New estimates and QA readiness still govern the replacement sprint. A scope amendment never marks delivery accepted or missing measurements passed, and cannot edit an active assignment. Repeating the same chat turn recovers the durable request; a different direction at the same review boundary is rejected instead of overwriting it.
## Large planning handoffs (2.11.2)

`BoundPlanningHandoff` keeps ordinary requests in their initial message and carries oversized requests in the same planning-cycle artifact's `coordinationContext`. The coordination start persists both atomically. Exact originals, replacements, owner authorization and prior management decisions remain intact; no text is truncated. Technical Director 2.11.9 is required to read artifact-carried context. The artifact remains bounded, references retain their revisions, and replay uses the existing session key. Install the Director update before resuming the blocked Producer planning commitment.

## Raising decisions to the owner (2.14.0)

When a ticket on a board I manage is Blocked with the `decision-required:v1` diagnostic, I send my manager one direct message per blocked attempt. This happens when a developer or QA reports that no code change can move the ticket and it needs a scope, criteria, environment or tooling decision. The message includes the specialist's decision request and the exact replies I act on: `Amend ticket <ID>: <decision>` to change or defer criteria through the normal replanning, estimate and readiness flow, or `Retry ticket <ID>: <what changed>` once the missing environment or direction exists. Ordinary blockers are not relabelled as decisions, and a single blocked attempt is never escalated twice. There are no new grants.

## Project incident reporting

Project-health and management-incident events are handled before ordinary workflow routing. The Producer
reads sanitized project-scoped evidence and reports observed facts, likely causes, missing evidence, and a
recommended action without invoking a model. Management agents forward operational failures outside their
responsibility using the same incident identity. Attention reviews recover pending incidents after reconnect.

The manifest requests incident read/forward capabilities; the Producer additionally requests health,
diagnostics, and report capabilities. Approve these through the normal installation grant review. The
platform enforces current project/reporting authority and advances unhandled hops after 15 minutes.
No additional repair or automatic retry authority is requested.


## Shared manager type

The manifest declares `rolePolicy.baseType: "manager"` and `profile: "manager.v1"`.
This agent derives from SDK `CSweetManagerAgentBase`; its job remains a specialized manager role.
The shared base handles project-health/incident events and attention recovery before ordinary work.
Diagnostic reads and assessment reports require the manifest's current approved project-health and
incident grants. Monitoring covers current assigned projects only. The default diagnosis escalates;
role-specific recovery can be added through `AssessIncidentAsync` using existing authorized operations.
A recorded recovery request does not close the incident or extend its 15-minute escalation deadline.

## Awaited handoff follow-up (2.15.0)

Before any project is visible, the Producer's next step is its manager's handoff. `producer-handoff-watch`
records the manager, owner conversation and follow-up history. Attention reviews without a project call
`FollowUpAwaitedHandoffAsync`, which nudges the manager after 30 minutes of silence (twice for an agent
manager) and then reports the stall once to the owner. Nudges are direct messages, which the Creative
Director treats as a kickoff wake, so the handoff is retried rather than waiting for another event.

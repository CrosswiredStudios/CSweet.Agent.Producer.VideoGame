# Video Game Producer

Leads project delivery, boards, sprints, schedules, dependencies, staffing evidence, risks, and status reporting. The Producer follows the actual reporting manager. A short manager brief can start a project proposal and team plan without a Creative Director, pitch, or GDD. The Producer manages delivery but does not make spending or hiring decisions for their respective authorities.

`AdvanceProgressivePlanningAsync` publishes the complete approved scope outline before requesting one bounded Story's executable briefs from the Technical Director. The lightweight planning path negotiates this behavior when no dedicated Designer or active role repair is involved. Frozen container identities and criteria cannot be rewritten by an increment. Existing estimation, staffing, QA readiness and preflight remain required. `StaffWorkflowAsync` uses SDK 5.0.4 `PrepareIncrementAsync` to scope delivery to fully detailed increments; future milestones remain on the board. The durable planning commitment advances only after canonical task completion and completed aggregate delivery acceptance, with idempotent coordination requests and bounded reconnect discovery. No new grants are requested.


`ProgressivePublishedItems` ignores unrelated human follow-up title prefixes and rejects duplicate authoritative proposal identities. Staffing preserves the active increment's exact Epic set. `ReadProgressiveContinuationAsync` wakes accepted increments through current-state reads, with durable revision-bound recovery receipts that prevent duplicate events from resetting failed retries. Deferred release QA stays on the board. `ProgressiveSprintHandoffTests` covers the successor path through estimates, readiness and preflight to a single sprint start.

## Coordination flow (2.18.0)

`WorkflowStaffing.StaffWorkflowAsync` derives the complete delivery path from the
approved board policy, binds eligible team members and preserves every started
assignment. It supplies technical review, independent QA and merge authorization
even when the technical proposal only named an implementer. Sprint readiness
performs staffing before authoritative preflight.

`producer-workflow-recovery:` personal commitments handle active and paused
sprints as well as planned work. Work-item events and bounded reconnect reviews
discover changed board/roster state. Recovery uses the existing
`work.item.delivery.finalize` capability, retaining the published commit, scope,
accountable owner, completed stages and attempt history. Only the board manager
may change future assignments; started work requires cancellation/replanning.
Missing staffing is repaired automatically when eligible members are available;
uncovered roles follow existing hiring approval. Other blockers keep their
evidence and follow bounded retry or management escalation. No new permissions
or event subscriptions are requested. Deploy with the matching platform recovery
validation and sprint-preflight changes.

`HandleManagerMessageAsync` acknowledges authenticated teammate check-ins and reconciles authoritative readiness without model inference. Manager setup decisions remain restricted to reporting authority. Manager responses use a 2048-token ceiling; full planning documents keep their configured budget. `RefinePitchAsync` supplies conversation text, the latest creative edits and the working brief while retaining accepted-source evidence in the saved document. Platform planning-cycle wakes and reconnect recovery release deferred commitments without waiting for the five-minute attention review.

`ReconcileSprintReadinessAsync` finalizes the selected executable leaves before sprint preflight through `FinalizeSprintDeliveriesAsync`. It preserves the current planning requirements, acceptance criteria, constraints, dependencies, accountable owner and exact stage assignments. Repository selection reuses an existing sprint binding or the approved team's sole repository option with a default branch; ambiguous or incomplete prerequisites remain pending. Existing delivery specifications are preserved. The host still validates authority, approvals and revisions, and the sprint starts only after authoritative preflight passes. Reconnect and retry resume partial finalization without duplicating delivery.

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
- Version: `4.0.5`

Pitch refinement stops after four Producer reviews or a repeated unchanged objection, retaining the
existing brief and unresolved questions for manager resolution. A new recorded owner decision permits
another bounded window; exact approval can still finish a retained ready brief. Stall reports use a
single compact model call (1,600 output tokens, no tools) and persist by incident/current-evidence hash.
Unchanged evidence and failed report generation reuse the saved report across restarts. Normal delivery,
authorized decision recovery and platform escalation deadlines remain authoritative.

Progressive outline verification includes the Technical Director's empty `DependencyPins` ledger in the frozen scope digest. The exact saved outline can advance without regeneration; altered scope still fails validation. Terminal progressive validation/refinement failures retain the recovery marker, so attention wakes do not repeatedly requeue unchanged work. Retry remains explicit after the cause is corrected. The matching platform classifies historical digest failures as technical issues and uses the normalized report's owner-input policy.

Planning generation and validation failures retain the specialist diagnostic and are reported as C-Sweet technical issues. Gabriel forwards these through the reporting manager to the CEO for code correction; a failed output-size/schema check is not a creative disagreement or an authorization to drop scope. The matching platform preserves this diagnosis, publishes durable manager handoffs, and delivers an informational CEO report without inventing a creative decision or claiming that a retry ran.

- Project planning questions go to the Creative Director through delegated `work-planning` decisions. Recorded manager direction wakes a new specialist planning pass; only a material escalation reaches the CEO.
- Provides: `work.execution.run`
- Activation: always on, with five-minute attention reviews
- Requested platform/provider capabilities: typed planning, work management, governed staffing, communication, artifacts and brokered model access (see manifest).
- Event subscriptions: attention, coordination, workstream, workforce, hiring, work-item, sprint, and management changes
- Network access: none

`QaReportPlanning` explicitly classifies QA-owned validation reports as Artifact delivery.
`StaffWorkflowAsync` repairs legacy unstarted report planning through revision-checked platform
operations before hierarchical staffing, preserving scope, dependencies and provenance. Bound
delivery, active/historical execution and completed work require normal manager replanning.
The Producer reviews exact report evidence; implementation still receives independent QA.

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.Producer.VideoGame -- --self-test
```

The tests run entirely in memory and require no C-Sweet instance or credentials.

The installation settings **Maximum context-window tokens** (`maxContextWindowTokens`, default
256,000) and **Maximum output tokens** (`maxOutputTokens`, default 128,000) configure the Producer's
planning ceiling and per-response output budget for pitch refinement, delivery review, and
specialist work. The output budget includes model reasoning and must remain below the context
ceiling. The agent imposes no fixed maximum, so both values can match the selected model. These
settings do not enlarge the model's actual context window or override the provider profile's
authoritative execution ceiling.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

For normal upgrades, publish the committed release and use **Installed Agents** to review and
install it. Local test/publish outputs are verification artifacts; copying them into an installed
package can separate the approved manifest from the running executable. The build checks the
project/manifest version, runtime identity comes from build metadata, and release CI self-tests
the published executable before packing. If a hire reports a different approved version, update
through Installed Agents and open a fresh hiring review; an old retry retains its reviewed package.

Built with `CSweet.Agent.SDK` 5.0.4 and `CSweet.WorkManagement.Contracts` 5.1.0, plus the bundled video-game extension source.


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

Uses CSweet.Agent.SDK 4.1.1 typed document references, explicit coordination read sharing,
accepted-revision lookup, and handoff readiness checks. Pitch content and staffing judgment
remain agent-specific. See the SDK's `docs/collaboration.md` for reusable documentation requests,
clarification, review, and personal-work dependency waits. The matching C-Sweet host is required
for sharing at every coordination start; package import alone does not update the host.

## Provider queue handling

Uses SDK 4.1.1 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Hiring kickoff

Onboarding introduces the Producer only to the authoritative manager (including a CEO who directly
manages the Producer), and persists the kickoff before acknowledging the lifecycle event. A hiring
CEO who is not the manager receives no onboarding introduction. Stable message
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

## Ticket discussion (2.18.0)

Resumes a blocked discussion wait only after the requested installation replies to the exact question revision, retaining assignment revision checks and attempt budgets. Reply text never grants approval or changes acceptance criteria.

The agent subscribes to `com.csweet.work.item.discussion.changed.v1` and uses grant-governed item,
comment, board and orchestration reads. Ask with `@Full Name: your question` on a team ticket.
Replies use `discussion.reply` and an exact comment/revision correlation; duplicates and stale
requests are ignored. Informational notifications do not trigger automatic conversation loops.
Bounded attention recovery scans the current assigned project for missed requests. The host retains
undelivered notifications while offline; install the matching C-Sweet host update and approve new
manifest permissions. Comments provide context, never execution authority or review approval.

## Hierarchical delivery

The V2 assignment identifies Task, Story, Epic or Release scope. Tasks require an authorized sprint; aggregate reviews require an active delivery plan and do not invent sprint identities. Use the exact assigned branch, commit or artifact revision. Every task requires independent QA, including documents. Managers accept aggregate candidates using current evidence; task completion does not promote to main or authorize deployment.

## Manager-reviewed project proposals

The Producer can submit projects from a lightweight reporting-manager brief or a Creative Director's accepted project template. The reporting manager reviews the stored command; the Producer revises requested changes and waits for authoritative project creation before delivery. `platform.project-approval.read.v1` reads submitted review outcomes; `com.csweet.project-approval.decided.v1` wakes recovery, with a bounded durable proposal index covering missed events. Model refinements preserve profile, team, lead, evidence and authority. Spending defaults to unlimited while real-money spending is unavailable; manager review still enforces any configured positive delegated budget limit.

QA plans/reports are reviewed by the Producer as exact evidence artifacts. One QA agent can author them and independently verify the implementation. Missing workflow staffing is surfaced as a task-specific blocker. No additional grants are requested.

## Certified release build-readiness

SDK 4.1.1 allows the Producer to assign build-readiness to an updated Technical Director or Developer when no Build Release Engineer is available. The assigned agent must advertise `work.execution.build-readiness.v1` and V2 execution. `DeliveryBuildReadiness.ExecuteAsync` requests the configured certified provider build for every exact release repository candidate, returns durable pending build identities, and verifies successful output provenance. It grants no deployment, publication, or independent product approval authority. Existing project, repository and build grants remain required.

## Project compute test delivery

- Prefer a qualified Technical Director's compute test path for game sprint setup, removing the certified-toolchain prerequisite.
- Keep independent code review and QA gates, and preserve recoverable missing-option waits instead of exhausting task retries.
- Consume Agent SDK 4.1.1 and WorkManagement.Contracts 3.27.0.

Current compute review links are local to the provider machine. Public hosting remains a separate capability. Pending setup and local-link approval preserve durable progress; unknown command outcomes are not automatically repeated.

Producer 2.19.7 uses SDK 4.1.1 and WorkManagement.Contracts 3.28.0 to propose independent manager reviews for QA report artifacts before draft plan creation. Configure, finalize and activate conflicts remain visible staffing waits. The QA proposal policy generation wakes retained recovery commitments after the update; Retry staffing also wakes sprint preflight. Update the platform before this agent; finalized assignments and independent product QA remain required.

## Planning and delivery recovery (2.20.0)

Board wake hints respect recorded recovery and sprint preflight retry deadlines, including when a source fingerprint changes. Recovery generations use planning and scope changes instead of generic task revisions. SDK 4.1.1 compares stage assignments independently of database return order. The platform must also connect the execution gateway to GitHost to activate delivery branches. Recorded workflow staffing waits are visible to project health and escalate through manager incident reports.

An exact manager decision to continue the accepted brief closes its question without another full specialist proposal. Material direction still creates a revision request. Completed proposals are retained with their exact accepted package; routine backlog reviews preserve finalized hierarchical staffing. SDK 4.1.1 refreshes stale Draft scope pins and only affected deliveries in place. The updated recovery fingerprint wakes retained staffing commitments. After updating the platform and installed Producer, use the authorized `Retry staffing: updated draft reconciliation` command if an existing preflight remains asleep; authoritative preflight still decides whether a sprint may start.

## Delegation and tracking

Producer 2.20.2 re-reads the project roster during staffing-event and attention/reconnect discovery. An eligible Technical Director releases the matching initial hiring wait immediately. Other timed recovery waits, foreign team contexts and different planning fingerprints retain their deadlines. The existing platform requeue endpoint remains authoritative for claims, permissions and revisions.

SDK 4.1.1 dispatches authenticated delegation before specialist conversation fallback. The manifest requests delegation, work tracking, directory and conversation reads, personal commitments and model interpretation. These declarations require reviewed grants; existing resource permissions and approval rules still apply. The shared SDK owns coordinator reporting and wake recovery.

Producer 3.0.0 gives each sprint a distinct creation identity and estimate/QA cycle. Completion and reconnect discovery recover successor preparation even when an older commitment was completed with the previous sprint's result. Active sprints and terminal tickets are preserved; current QA readiness and platform preflight remain required.

## Execution contract (3.0.0)

Execution uses one `work.execution.run` capability and `WorkExecutionAssignment` from SDK/WorkManagement.Contracts 5.0.1. Task and aggregate scopes retain their distinct authority checks within that envelope. Upgrade the platform and executing agents together; no legacy execution adapter remains.

## Specific planning blockers (3.0.1)

`SpecialistAgent.DescribePlanningBlocker` reports each unresolved question, its current decision ID,
status, selected option, recorded rationale and next action, with the production lead responsible
for follow-up. A request for evidence remains unresolved even when its decision status is Decided;
an exact continuation decision removes only its matching question. A revised accepted brief is
required only for an actual scope change.

`ProducerPlanningCycleState.AuthorityDecisionFingerprint` retains the exact unresolved-question
and decision snapshot. `EnsureCommitmentAsync` re-reads authorized current state on existing
decision events and bounded attention/reconnect reviews, retaining an unchanged planning blocker
instead of requeueing it every five minutes. A changed decision, missing record, changed scope or
legacy state permits reassessment; explicit Retry remains available. This does not approve scope
or change the current research-spikes-only sprint restriction while questions remain open.

## Shared manager blocker reporting

This release uses SDK 4.1.1 `ManagerIncidentCoordinator` for evidence-based incident reports and human-selected fixes. The shared SDK dispatcher runs before this agent's specialist intake, coordinates authorized recovery or delegation, and preserves required approvals. The incident presentation/update and resolution/read grants plus `com.csweet.management.incident.owner-report-requested.v1` allow existing open incidents to receive refreshed reports. Apply the platform migration before upgrading.

## Production prerequisite recovery

The Producer carries current, accepted-vision-bound asset strategies into planning, including procedural generation requirements without dropping accepted deliverables. A coverage proposal rejected only for stale flow metrics receives one evidence refresh with stable replay and the same approved staffing baseline. Hierarchical staffing recovery rereads outstanding task coverage and uses the existing governed resource-change path. Office image compatibility remains a platform prerequisite; adding duplicate employees cannot repair an unavailable environment.

Activation defaults to OnDemand. Durable messages, assignments, scheduled duties and attention reviews wake the agent; idle runtimes release their Office slot. Attention wakes require the platform scheduler that supports sleeping OnDemand installations. Existing installations retain their selected activation mode until their schedule is updated.

## Small tasks and comment revisions

Require small independently deliverable tasks and Technical Director implementation briefs in both planning flows. Resume revision waits only after an explicit correlated correction comment. Keep clarification/replacement requests blocked, escalate replacement recommendations, and prevent plain retries from bypassing them. Comments can supersede specific implementation guidance, but cannot waive accepted outcomes, criteria, reviews or grants. Substantial changes require linked replacement tickets through governed replanning; a replacement recommendation is not automatic cancellation or execution authority. The original stays blocked until that planning is applied.

## Forecast-driven planning

Use forecast-driven planning: absent deadlines never block brief readiness or staffing proposals. Technical feasibility and estimates refine forecasts after authorized staffing. Incorporate authoritative answers relayed by Naomi and reuse unchanged brief revisions. Exact-revision acceptance remains required.

Package dependencies: WorkManagement.Contracts 5.1.0 and SDK 5.0.4. No deadline is invented and no date is required to submit an otherwise sufficient brief for creative acceptance.

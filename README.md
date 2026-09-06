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
- Version: `2.2.0`
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

Built with `CSweet.Agent.SDK` 3.28.0 and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Progressive delivery (2.2.0)

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

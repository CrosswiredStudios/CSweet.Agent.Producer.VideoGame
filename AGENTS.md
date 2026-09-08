# Video Game Producer repository instructions

This repository contains one standalone C-Sweet protocol-v2 agent. Its purpose is:

> Leads project delivery, boards, sprints, schedules, budgets, dependencies, staffing, risks, and status reporting.

## Invariants

- Keep `com.csweet.video-game-producer` and the current package version synchronized between agent
  code, `csweet-plugin.json`, tests, and releases.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events,
  configuration, credentials, web access, and UI contributions synchronized with implementation
  and tests.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access
  runtime/workload/lease tokens, connect directly to databases or Docker, or handle provider
  credentials.
- Agent work is delivered at least once. Honor cancellation and use stable domain idempotency keys
  for external mutations.
- Unknown capabilities and events must fail or be ignored safely without leaking sensitive data.

## Verification

Run from the repository root:

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.Producer.VideoGame -- --self-test
```

Any new capability, grant, event, configuration field, credential, or network rule requires a
manifest update, a README explanation, and tests.

## Release-note ordering

- Bump the agent version FIRST, synchronizing the root `csweet-plugin.json`, implementation identity, project/package version, and version assertions as required by this repository.
- Only AFTER the version bump, read the final `version` back from `csweet-plugin.json` and write `releases/<version>.md` for that exact version (no `v` prefix). Never write the new release's notes under the previous version.
- If the version changes again during the task, retarget the unpublished notes to the final version. Preserve already published historical notes.
- Before handoff or publishing, verify that the manifest, implementation/package version, release-note filename, and release-note heading all match. A version bump is incomplete without its matching release notes.

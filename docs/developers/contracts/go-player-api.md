# Go player API

[Subsystem contracts](README.md)

The gated player service uses one Player, session, writer and current-control coordinator. The surface
is guidance: building placement and research selection are the only typed plan submissions; the rest is
colony configuration and control. There is no per-pawn order surface (draft, tend, rescue, movement,
surgery, bed assignment, husbandry, recovery service, building temperature, caravans, quests,
settlement gifts, trade, zone edits, room shells); those action families exist only as routine-planner
output.

## Routes

`rimgovernor serve --profile PATH` (autonomous play) includes the explicit player service. The
read-only service exposes none of these player routes.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/api/player/session` | Process token and `mode: "explicit-player"` |
| GET | `/api/player/control` | Current control record and actual permission |
| POST | `/api/player/control/resume` | Run the bot for the exact observed world under that world's root plan |
| POST | `/api/player/control/pause` | Stop the bot: invalidate local permission, suspend routine concerns and clean up owned work |
| GET/POST | `/api/player/clock`, `/api/player/clock/acknowledge` | Clock review |
| GET | `/api/player/colony` | Live colony census (below) |

`/api/player/colony` fields; unknown facts are null:

- food nutrition and runway, colonists, workers, downed, mood mean;
- the living home roster: each colonist's `id`, `label`, `downed`, `mood`, `food`, and the read-only
  personal wealth share `share`, what is attributed to them now `spent`, and `remaining` (null until a
  review has filed fresh colony facts, for a slave's spent, and for any unread input);
- raid points and the wealth split: `raidPoints`, `wealthTotal`, `wealthItems`, `wealthBuildings`,
  `wealthPawns`;
- the ancient shrine census `shrines`: id, `sealed`, `inHome`, `caskets`, `filledCaskets`,
  `guardsKnown`, `guardsAlive`, `breachWalls`, plus the breach judgement `ready`, `reason`, `wall`,
  `squad`, `traps`.

Mutations require JSON and the process token in `X-RimGovernor-Player`. Bodies are bounded to 8192
bytes; duplicate, unknown, null, malformed and trailing fields are rejected. Errors keep the sanitized
code/detail and control record/state/error shapes.

## Submissions

Building submission accepts exactly:

```json
{
  "requestId": "player-request-id",
  "expected": {"colonyId": "colony", "loadToken": "load", "mapId": 0},
  "building": {"defName": "Wall", "stuff": "WoodLog", "x": 0, "z": 0, "rotation": "north"}
}
```

Research selection uses the same envelope with `select: {"project": "exact defName"}` in place of
`building`.

- Each response echoes those fields and adds `planId`, `actionId` and `revision` (a positive canonical
  decimal uint64 string). A newly stored request returns 201; an exact replay or successful lookup
  returns 200. Request IDs share one namespace across submission kinds; conflicting reuse returns 409.
- Submission never acquires authority; native eligibility and CAS are resolved by fresh executor
  inspection when a player enables the plan.
- A missing lookup is not proof that a timed-out POST had no effect; clients recover by reading the
  same request ID, never by automatic POSTs.

## Control semantics

Resume and Pause carry `requestId` and `expected` (the exact world) only: no plan reference, no
direction compare-and-swap. Intents are journaled in order and each `requestId` is durable.

- Resume creates or reuses the world's empty root plan (`root/<colony>/<load>/<map>`, revision 1) and
  runs the bot under it; the root plan holds no actions. Routine methods and player submissions
  (building, research) for the same world are dispatched under the root's authority once
  `AuthorizeRoundsPlan` or `AuthorizePlayerPlan` accepts them. A submission is guidance the running
  bot executes, not a grant of its own; there is no priority arbitration between guidance and routine
  work.
- Pause stops local work; drafted pawns stay drafted until the game's own auto-undraft takes them back.
- Record phases: `pending`, `running` (resume, non-zero native generation), `paused`, `refused`,
  `uncertain`. Historical results never confer current permission; `state.generation.plan` is the live
  root plan.
- Go surface: `httpapi.PlayerControl` is `Resume`, `Pause`, `State`; its reader
  (`httpapi.ControlReader`) is `CurrentControl`. Configuration routes have their own narrow
  interfaces. Production composition supplies the same Player and store the worker uses.

An expedition may carry a running Auto intent to its physically reached map.
The accepted departure in the retained method journal must name the exact
destination tile and crew, and the fresh world census must place that crew
there. A site also needs its quest identity to match the journal method.
The handoff holds the player gate and captures the current player epoch before
disabling local writes and joining writers. Its Resume intent is journaled
before any native view change; it uses the destination's normal root plan.

Trusted `Authority.Control/FocusMap` compares the exact viewed colony, load,
map and native generation, requires active Auto authority, and focuses an
already loaded map in the same colony/load. It moves no pawns, grants no
authority and is unavailable to model dispatch. Native requests a fresh
snapshot keyframe after focusing. Normal identity-scoped Resume reacquires
authority; the focus generation must still match at acquisition.

Vanilla may remove an emptied site map and focus home before the review runs.
The scheduler handles this from the incoming frame's actual identity and exact
returning crew, without reading the removed map. Reacquisition requires native
`IDENTITY_CHANGED` at precisely the prior Auto generation plus one. A Manual
request cancels the captured player epoch before waiting for the gate; a native
Manual changes the generation/revocation reason. Either prevents automatic
regrant, including between focus and acquisition. Historical control records
alone never restore authority after restart.

## Colony configuration

Configuration routes record player intent and issue no per-pawn order. The autopilot owns the
population target and resource stock targets, so there are no population-policy or resource-policy
routes. Work preferences attach to the live root plan.

## Plan projection

`GET /api/plan?id=…` returns the plan envelope. Each action is one of:

- `kind: "building"` with `building` and no `draft` payload;
- `kind: "owned_draft"` (routine-produced) with `draft: {"pawnId": "…"}` and no `building` payload;
- any other routine kind with neither payload.

The public projection omits leases, native tokens and controller-session ownership data. An
`owned_draft` action is plan-owned: the undraft sweep undrafts its pawn once no live plan needs it, so
drafts carry no cleanup progress and are not a persistent toggle.

## Launcher behavior

The launcher's Resume runs the bot for the observed world and Pause stops it
([launcher](../architecture/launcher.md)). An unresolved Resume (pending or uncertain) blocks another
Resume until a later journaled Pause supersedes it; Pause is always available. Background refresh
preserves request IDs and last-good data. Session/world changes exclude stale permission and responses
without silently resubmitting either intent. The UI has no draft, undraft or native-token input.

Draft and undraft through the draft intent are covered by native acceptance `draft/intent`.

## Concern JSON names

Read routes name the watched kind `concern` (a Concern id string such as `EnsureFoodSupply`):
`GET /api/routines` carries `progress[].concern`, `noOps[].concern` and `development.rows[].concern`;
`GET /api/spectator/now` carries `concerns[]` with `concerns[].concern`. Other fields (`method`,
`expected`, `lastProgress`, `nextReview`, `blocked`, `cooldowns`, `prerequisite`, ...) are as named.

## Finding value strings

| Where | Values |
| --- | --- |
| `domain.Finding` (Standard / Project `Finding`, disaster service `Need`, acceptance and colony-review `need` samples) | `unclear` / `unmet` / `met` |
| `domain.Situation` (Incident bindings) | `unclear` / `active` / `clear` |

The HTTP read routes (`/api/routines`, `/api/spectator/now`) carry no finding string; the values reach
clients through the colony-review timeline (`need` per concern) and the acceptance reports.

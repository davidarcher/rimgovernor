# Go player API

[Subsystem contracts](README.md)

The gated player service uses one Player, session, writer and current-control coordinator. The
surface is guidance: building placement and research selection are the only
typed plan submissions; the rest is colony configuration and control.

## Routes and intent

`rimgovernor serve --profile PATH` (autonomous play) includes the explicit player service. Shared
control uses these routes.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/api/player/session` | Process token and `mode: "explicit-player"` |
| GET | `/api/player/control` | Current control record and actual permission |
| POST | `/api/player/control/resume` | Run the bot for the exact observed world under that world's root plan |
| POST | `/api/player/control/pause` | Stop the bot: invalidate local permission, suspend routine goals and clean up owned work |
| GET/POST | `/api/player/clock`, `/api/player/clock/acknowledge` | Clock review |
| GET | `/api/player/colony` | Live colony census: food nutrition and runway, colonists, workers, downed, mood mean, the living home roster (each colonist's `id`, `label`, `downed`, `mood`, `food` and the read-only personal wealth share `share`, what is attributed to them now `spent`, and `remaining`, #1846; null until a review has filed fresh colony facts, for a slave's spent and for any unread input), raid points and the wealth split (`raidPoints`, `wealthTotal`, `wealthItems`, `wealthBuildings`, `wealthPawns`; #395) and the ancient shrine census (`shrines`: id, `sealed`, `inHome`, `caskets`, `filledCaskets`, `guardsKnown`, `guardsAlive`, `breachWalls`; #456; with the breach judgement `ready`, `reason`, `wall`, `squad`, `traps`; #457) (unknown facts are null) |

Mutations require JSON and the process token in `X-RimGovernor-Player`. The
read-only service exposes none of these player routes. Old building control
routes and the old control flag have no aliases.

Building submission accepts exactly this shape:

```json
{
  "requestId": "player-request-id",
  "expected": {"colonyId": "colony", "loadToken": "load", "mapId": 0},
  "building": {"defName": "Wall", "stuff": "WoodLog", "x": 0, "z": 0, "rotation": "north"}
}
```

Research selection uses the same envelope with `select: {"project": "exact defName"}`
in place of `building`. Each response echoes those fields and adds `planId`,
`actionId` and `revision` (a positive canonical decimal uint64 string). A newly
stored request returns 201; an exact replay or successful lookup returns 200.
Request IDs share one namespace across submission kinds. Conflicting reuse returns
409. Submission never acquires authority; native eligibility and CAS are resolved
by fresh executor inspection when a player enables the plan.

There is no per-pawn order surface (draft, tend, rescue, movement, surgery, bed
assignment, husbandry, recovery service, building temperature, caravans, quests,
settlement gifts, trade, zone edits or room shells). Those action families exist
only as routine-planner output; the routes, store tables and executor states for
their player submissions were removed in
[issue #54](https://github.com/davidarcher/rimgovernor/issues/54).

Resume and Pause carry `requestId` and `expected` (the exact world) only;
there is no plan reference and no direction compare-and-swap. Intents are
journaled in order and each `requestId` is durable. Resume creates or reuses
the world's empty root plan (`root/<colony>/<load>/<map>`, revision 1) and
runs the bot under it; the root plan holds no actions. Routine methods and
player submissions (building, research) for the same world are dispatched
under the root's authority once `AuthorizeRoundsPlan` or
`AuthorizePlayerPlan` accepts them — a submission is guidance the running
bot executes, not a grant of its own, and there is no priority arbitration
between guidance and routine work. Pause stops local work; drafted pawns stay
drafted until the game's own auto-undraft takes them back (#939). Record phases are `pending`, `running` (resume, non-zero
native generation), `paused`, `refused` and `uncertain`. Historical results
never confer current permission; `state.generation.plan` is the live root
plan. Bodies
remain bounded to 8192 bytes, with duplicate, unknown, null, malformed and trailing
fields rejected. Errors retain the existing sanitized code/detail and control
record/state/error shapes. Missing lookup is not proof that a timed-out POST had
no effect; clients recover by reading the same request ID without automatic POSTs.

## Colony configuration

Configuration routes record player intent; they issue no per-pawn order.
The population target belongs to the autopilot; there is no population-policy
route. The autopilot owns resource stock
targets; there is no player resource-policy route. Work
preferences attach to the live root plan.

## Plan projection

`GET /api/plan?id=…` returns the existing plan envelope. Each action is one of:

- `kind: "building"` with `building` and no `draft` payload.
- `kind: "owned_draft"` (routine-produced) with `draft: {"pawnId": "…"}` and no
  `building` payload.
- any other routine kind with neither payload.

Ordinary `progress` keeps its current fields. The public projection omits leases,
native tokens and controller-session ownership data. Drafts carry no cleanup
progress: the undraft sweep releases drafts no live plan needs (#939).

The Go player surface (`httpapi.PlayerControl`) is
`Resume`, `Pause` and `State`; its reader
(`httpapi.ControlReader`) is `CurrentControl`. Configuration routes
have their own narrow interfaces. Production service composition supplies the
same Player and store used by the worker.

## Dashboard behavior

The building form shares token bootstrap, current permission and
the Resume/Pause history. Submissions are guidance: the forms no longer enable
a plan; the heading's Resume runs the bot for the observed world and Pause
stops it. An unresolved Resume (pending or uncertain) blocks another Resume
until a later journaled Pause supersedes it; Pause remains available
regardless. Background refresh
preserves both forms, request IDs and last-good data. Session/world changes
exclude stale permission and responses without silently resubmitting either
intent.

A routine-produced `owned_draft` action is plan-owned: the undraft sweep
undrafts its pawn once no live plan needs it (#939); it is not a persistent
draft toggle. The UI has no draft, undraft or native-token input.


## Native draft acceptance

Draft and undraft through the draft intent are Go native acceptance in
`draft/intent`.

## Concern JSON names (epic #1964, #1977)

The read routes name the watched kind a `concern` (a Concern id string such
as `EnsureFoodSupply`; the id strings did not change). The old `goal` and
`goals` keys are gone, with no compatibility alias.

| Route | Field | Was |
| --- | --- | --- |
| `GET /api/routines` | `progress[].concern` | `progress[].goal` |
| `GET /api/routines` | `noOps[].concern` | `noOps[].goal` |
| `GET /api/routines` | `development.rows[].concern` (the development rows) | `...goal` |
| `GET /api/spectator/now` | `concerns[]` (array) | `goals[]` |
| `GET /api/spectator/now` | `concerns[].concern` | `goals[].goal` |

Every other field of those shapes (`method`, `expected`, `lastProgress`,
`nextReview`, `blocked`, `cooldowns`, `prerequisite`, ...) is unchanged.
Persisted store names follow the glossary since #1976.

## Finding value strings (epic #1964, #2002)

The tri-state finding strings changed everywhere they are stored or emitted
(no compatibility strings; store schema 196):

| Where | Was | Now |
| --- | --- | --- |
| `domain.Finding` (Standard / Project `Finding`, disaster service `Need`, acceptance and colony-review `need` samples) | `unknown` / `deficit` / `recovered` | `unclear` / `unmet` / `met` |
| `domain.Situation` (Incident bindings) | `unknown` / `deficit` / `recovered` | `unclear` / `active` / `clear` |

The HTTP read routes (`/api/routines`, `/api/spectator/now`) carry no finding
string; the value changes reach clients through the colony-review timeline
(`need` per concern) and the acceptance reports. Field names did not change.

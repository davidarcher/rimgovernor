# Player API

[Subsystem contracts](README.md)

This page describes the service. The C# port serves the session, control and clock routes without the
process token (host and origin checks only) from `RimGovernor.Brain.PlayerApi`; see the
[HTTP channel](../architecture/http-channel.md).

The gated player service uses one Player, session, writer and current-control coordinator. The surface
is guidance: building placement and research selection are the only typed plan submissions; the rest is
colony configuration and control. There is no per-pawn order surface (draft, tend, rescue, movement,
surgery, bed assignment, husbandry, recovery service, building temperature, caravans, quests,
settlement gifts, trade, zone edits, room shells); those action families exist only as routine-planner
output.

## Routes

The in-process host (autonomous play, [brain host](../architecture/brain-host.md)) includes the explicit player service. The
read-only service exposes none of these player routes.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/api/player/session` | Process token and `mode: "explicit-player"` |
| GET | `/api/player/control` | Current control record and actual permission |
| POST | `/api/player/control/resume` | Run the bot for the exact observed world under that world's root plan |
| POST | `/api/player/control/pause` | Stop the bot: invalidate local permission, suspend routine concerns and clean up owned work |
| GET/POST | `/api/player/clock`, `/api/player/clock/acknowledge` | Clock review |
| POST | `/api/player/clock-speed` | Set the speed the brain requests while it owns the clock (`{"speed":"Normal\|Fast\|Superfast\|Ultrafast"}`, else 400); the next step sends it, see [interface contracts](interface-contracts.md) |
| GET | `/api/player/colony` | Live colony census (below) |

`/api/player/colony` fields; unknown facts are null:

- food nutrition and runway, colonists, workers, downed, mood mean;
- the living home roster: each colonist's `id`, `label`, `downed`, `mood`, `food`;
- raid points and the wealth split: `raidPoints`, `wealthTotal`, `wealthItems`, `wealthBuildings`,
  `wealthPawns`;

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
- Resume and Pause also own the game clock, with no wire field for it: the bot holds the clock exactly while
  `state.enabled` is true. Resume lets the run driver send `Run` and own the speed; Pause holds the clock once
  (`Hold(Review)`, detail `bot_off`) and the player is then free to unpause. The bot does not re-take a clock
  the player runs while `state.enabled` is false, and the toggle is not persisted (`AutoResume` decides at load).
- While the bot holds the clock, native swallows the player's speed input. The owner flag is native control
  authority (`NativeControlAuthority` Active, granted by Resume, withdrawn by Pause or a revocation), not a
  wire field. `PlayerSpeedKeyBlock` marks `TimeControls.DoTimeControlsGUI`, where space, 1-4 and the time-control
  buttons live, and skips `TickManager.CurTimeSpeed` and `TogglePaused` changes made inside it
  (`SpeedKeyBlockPolicy.Swallow`). Letter and dialog pauses and supervisor assignments are outside that scope
  and pass. A blocked attempt shows a rate-limited in-game message saying the bot runs the clock and that
  Pause in the launcher takes over.
- Record phases: `pending`, `running` (resume, non-zero native generation), `paused`, `refused`,
  `uncertain`. Historical results never confer current permission; `state.generation.plan` is the live
  root plan.
- the brain surface: `httpapi.PlayerControl` is `Resume`, `Pause`, `State`; its reader
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

The public projection omits native tokens and brain-session ownership data. An
`owned_draft` action is plan-owned: the undraft sweep undrafts its pawn once no live plan needs it, so
drafts carry no cleanup progress and are not a persistent toggle.

## Control API and launcher

The Resume API enables control for the observed world and Pause stops it.
The [launcher](../architecture/launcher.md) starts the game with `RIMGOVERNOR_OBSERVE` off, which auto-resumes; its
Observe tab also has Resume and Pause buttons and shows `state.enabled` as Bot on or Bot off (the buttons post to these
routes for the world `state.generation` names). An unresolved Resume (pending or uncertain) blocks another
Resume until a later journaled Pause supersedes it; Pause is always available. Background refresh
preserves request IDs and last-good data. Session/world changes exclude stale permission and responses
without silently resubmitting either intent. The UI has no draft, undraft or native-token input.

Draft and undraft through the draft intent are covered by native acceptance `draft/intent`.

## Concern JSON names

Read routes name the watched kind `concern` (a Concern id string such as `MaintainResource`, or a NeedId `family/kind[/subject]` for a family's Need):
`GET /api/routines` carries `progress[].concern` and `noOps[].concern`;
Other fields (`method`,
`expected`, `lastProgress`, `nextReview`, `blocked`, `cooldowns`, `prerequisite`, ...) are as named.
`blocked` is a `policy.Cause` wire value (or `prerequisite:<concern>`); `subject` is its optional bounded subject, and the client renders both through `policy.Wording`.

## Finding value strings

| Where | Values |
| --- | --- |
| `domain.Finding` (Standard `Finding`, disaster service `Need`, acceptance and colony-review `need` samples) | `unclear` / `unmet` / `met` |
| `domain.Situation` (Incident bindings) | `unclear` / `active` / `clear` |

The HTTP read route `/api/routines` carries no finding string; the values reach
clients through the colony-review timeline (`need` per concern) and the acceptance reports.

## Return to the main menu

`POST /api/lifecycle/menu` with `{"requestId":"..."}` ends the loaded game without saving, the way
the player's quit-to-menu does, and leaves the process and the in-process brain running. When the
controller observes a game, the request names it, and native refuses to end a different one. It
answers 202 `{"status":"pending","requestId","detail"}` while the game unloads, and 201
`{"status":"completed","requestId","unloaded"}` once the game is at a fresh main menu, the state
`/api/lifecycle/new` starts from. `unloaded` is false when the game was already there. Poll
`GET /api/lifecycle/menu?requestId=`, which answers both outcomes with 200. Native refuses while
another long event, such as a load or save, is queued, or when the named identity does not match.
The in-process client raises that refusal, so the route answers 503 `unavailable`. After the
return, the state route reports no identity (`status` "No game is loaded"). The bot takes no action
until a colony map loads again.

## New colony scenario parts

`POST /api/lifecycle/new` accepts, beside the base `scenario` and the world settings, typed
scenario parts on `spec`: `startingThings` (`placement` `start_with` | `start_near` |
`scattered`, `thingDef`, optional `stuff` and `quality`, `count`, `allowRoofed` for scattered),
`forcedTraits` (`trait`, `degree`, `chance`), `forcedHediffs` (`hediff`, `severity`, `chance`),
`naked` (`chance`), `setNeedLevels` (`need`, `level`, `chance`), each pawn part with a `context`
(`all` | `player_starter` | `non_player`), plus the `permaGameConditions`,
`disallowedBuildings` and `disabledIncidents` defName lists and the overrides `playerFaction`
(`new_arrivals` | `new_tribe`) and `arrivalMethod` (`standing` | `drop_pods`). The starting pawn
count stays `colonistCount` (1-10). Ranges are in
[validation](../../../contracts/wire/validation.md#new-colony-scenario-parts); an out-of-range
or unknown-enum part is a 400 before native is called.

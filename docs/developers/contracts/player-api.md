# Player API

[Subsystem contracts](README.md)

This page describes the service. The C# port serves the session, control and clock routes without the
process token (host and origin checks only) from `RimGovernor.Brain.PlayerApi`; see the
[HTTP channel](../architecture/http-channel.md).

The gated player service uses one Player, session, writer and current-control coordinator. The surface
is guidance: colony configuration and control. There is no per-pawn order surface (draft, tend, rescue, movement,
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
| POST | `/api/player/clock-speed` | Set the speed the brain requests while it owns the clock (`{"speed":"Normal\|Fast\|Superfast\|Ultrafast\|Hyperfast"}`, else 400); the next step sends it, see [interface contracts](interface-contracts.md) |
| GET | `/api/player/colony` | Live colony census (below) |
| POST | `/api/telemetry/spans` | Launch phases timed outside the game, replayed into the brain's `traces.jsonl` (below) |

`/api/telemetry/spans` (`TelemetryRoutes`) takes the acceptance harness's and the launcher's launch
phases, because the in-process brain is the only writer of the run's traces ring. The body is a JSON
array of at most 32 `{"name","parent","startUtc","endUtc","attributes"}`: `parent` is `""` for a child
of the root or the name of an earlier span in the body, times are ISO-8601 UTC ending in `Z` with the
end not before the start, and `attributes` holds string values that become span tags. The brain
replays them as one `launch` tree under its own run id (a kept game's spans join that game's run) and
answers `{"spans": n}`; a malformed body is 400. Senders retry only while the route is not yet
registered (404) and drop the spans with a logged reason otherwise; spans never fail a run. See
[launch spans](otel-signals.md#launch-spans).

`/api/player/colony` fields; unknown facts are null:

- food nutrition and runway, colonists, workers, downed, mood mean;
- `roster`: `colonists` or `no_colonists`. An empty home roster is a 200 with `no_colonists` and an empty
  `pawns` list, not a read failure;
- the living home roster: each colonist's `id`, `label`, `downed`, `mood`, `food`;
- raid points and the wealth split: `raidPoints`, `wealthTotal`, `wealthItems`, `wealthBuildings`,
  `wealthPawns`;

Mutations require JSON and the process token in `X-RimGovernor-Player`. Bodies are bounded to 8192
bytes; duplicate, unknown, null, malformed and trailing fields are rejected. Errors keep the sanitized
code/detail and control record/state/error shapes.

## Control semantics

Resume and Pause carry `requestId` and `expected` (the exact world) only: no plan reference, no
direction compare-and-swap. Intents are journaled in order and each `requestId` is durable.

- Resume creates or reuses the world's empty root plan (`root/<colony>/<load>/<map>`, revision 1) and
  runs the bot under it; the root plan holds no actions. Routine methods for the same world are dispatched under the root's authority once
  `AuthorizeRoundsPlan` accepts them.
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

Read routes name the watched kind `concern`, a family Need's filing key (a NeedId `family/kind[/subject]`):
`GET /api/routines` carries `progress[].concern` (the `noOps` list is gone since #3265);
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

A failed start answers with its cause in `detail`: a native refusal by its code (`native_<code>`,
400 for an invalid request, otherwise 502), an unavailable bridge 503 `unavailable`, and a failure
of the brain's own resolution before native creation (the governor or theme ideoligion) 500
`new_colony_failed`.

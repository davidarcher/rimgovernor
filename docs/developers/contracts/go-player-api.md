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
| GET | `/api/player/control?requestId=…` | Historical request and actual permission |
| POST | `/api/player/control/resume` | Run the bot for the exact observed world under that world's root plan |
| POST | `/api/player/control/pause` | Stop the bot: invalidate local permission, suspend routine goals and clean up owned work |
| POST | `/api/buildings/plans` | Store one building intent |
| GET | `/api/buildings/submission?requestId=…` | Read a building submission |
| POST | `/api/research-selects/plans` | Store one research-selection intent |
| GET | `/api/research-selects/submission?requestId=…` | Read a research-selection submission |
| POST | `/api/chat` | Answer one plain-language message with an explanation and at most one applied policy nudge (goal activate/cancel, population, expedition, resource policy, population decision); never a build or order |
| GET/POST | `/api/player/goals`, `/api/player/goals/activate`, `/api/player/goals/cancel` | Maintained goal activation and cancellation |
| GET/POST | `/api/player/population-policy`, `…/replace` | Population capacity policy |
| GET/POST | `/api/player/expedition-policy`, `…/update` | Expedition risk limits |
| GET/POST | `/api/player/population-decision`, `…/replace` | Per-pawn population decision |
| GET/POST | `/api/player/resource-policy`, `…/update` | Resource reserve and spending restriction |
| GET/POST | `/api/player/work-preferences`, `…/replace` | Work preferences |
| GET/POST | `/api/player/clock`, `/api/player/clock/acknowledge` | Clock review |
| GET | `/api/player/world-evaluation` | Read-only caravan/quest evaluation |
| GET | `/api/player/colony` | Live colony census: food nutrition and runway, colonists, workers, downed, mood mean, the living home roster, raid points and the wealth split (`raidPoints`, `wealthTotal`, `wealthItems`, `wealthBuildings`, `wealthPawns`; #395) and the ancient shrine census (`shrines`: id, `sealed`, `inHome`, `caskets`, `filledCaskets`, `guardsKnown`, `guardsAlive`, `breachWalls`; #456; with the breach judgement `ready`, `reason`, `wall`, `squad`, `traps`; #457) (unknown facts are null) |

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
under the root's authority once `AuthorizeRoutinePlan` or
`AuthorizePlayerPlan` accepts them — a submission is guidance the running
bot executes, not a grant of its own, and there is no priority arbitration
between guidance and routine work. Pause stops local work and cleans up
owned drafts. Record phases are `pending`, `running` (resume, non-zero
native generation), `paused`, `refused` and `uncertain`. Historical results
never confer current permission; `state.generation.plan` is the live root
plan and is the `planId` that work preferences attach to. Bodies
remain bounded to 8192 bytes, with duplicate, unknown, null, malformed and trailing
fields rejected. Errors retain the existing sanitized code/detail and control
record/state/error shapes. Missing lookup is not proof that a timed-out POST had
no effect; clients recover by reading the same request ID without automatic POSTs.

## Colony configuration

Configuration routes record player intent; they issue no per-pawn order.
Population policy is a full replacement. Expedition policy is a patch merged
over the policy in force (an unset field keeps its value; a world with no
policy starts from the documented defaults). A population decision for
rescue, capture or recruit requires an established population policy; ignore
never does, so a direction can always be withdrawn. Resource policy has two
halves: spending (`normal`, `defense_only`, `stop`) and a numeric reserve
(zero removes the floor). A request names exactly one half and the other is
preserved; every change dispatches the whole merged set as one native
production-policy write. Work preferences attach to the live root plan.

## Plan projection

`GET /api/plan?id=…` returns the existing plan envelope. Each action is one of:

- `kind: "building"` with `building` and no `draft` payload.
- `kind: "owned_draft"` (routine-produced) with `draft: {"pawnId": "…"}` and no
  `building` payload.
- any other routine kind with neither payload.

Ordinary `progress` keeps its current fields. When draft cleanup evidence exists,
it also contains `draftCleanup: {"stage": "…"}`. The field is absent before draft
dispatch and for every other kind. Stages are `awaiting_claim`, `not_acquired`, `required`,
`dispatched`, `uncertain`, `released` or `superseded`. Cleanup does not change an
already recorded ordinary outcome. The public projection omits claim IDs, leases,
native tokens and controller-session ownership data.

The Go player surface (`httpapi.PlayerBuildings`) is `Submit`,
`SubmitResearchSelect`, `Resume`, `Pause` and `State`; its reader
(`httpapi.ControlReader`) is `CurrentControl`, `LookupControl`,
`LookupSubmission` and `LookupResearchSelectSubmission`. Configuration routes
have their own narrow interfaces. Production service composition supplies the
same Player and store used by the worker.

## Dashboard behavior

The building and chat forms share token bootstrap, current permission and
the Resume/Pause history. Submissions are guidance: the forms no longer enable
a plan; the heading's Resume runs the bot for the observed world and Pause
stops it. An unresolved Resume (pending or uncertain) blocks another Resume
until a later journaled Pause supersedes it; Pause remains available
regardless. Background refresh
preserves both forms, request IDs and last-good data. Session/world changes
exclude stale permission and responses without silently resubmitting either
intent.

Show ordinary progress and cleanup separately. A routine-produced owned draft
releases its claim when its plan finishes; it is not a persistent draft toggle.
The UI has no draft, undraft, claim-adoption or native-token input.


## Native draft acceptance

Draft claim/release evidence (ordinary completion, player override,
transport-error recovery, HTTP response-body loss followed by lookup, and
disabled same-database restarts) is Go native acceptance tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).

# Go player API

[Subsystem contracts](README.md)

This is the fixed interface for G01.07a.2f.2–3. Service availability and native
acceptance remain tracked in
[G01.08](https://github.com/davidarcher/rimgovernor/issues/29). The gated player
service uses one Player, session, writer and current-control coordinator for all
action families. Production launchers remain on Python until the runtime switch.

## Routes and intent

`rimgovernor serve --player-control` selects the explicit player service. Shared
control uses these routes; building submission routes remain family-specific.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/api/player/session` | Process token and `mode: "explicit-player"` |
| GET | `/api/player/control` | Current control record and actual permission |
| GET | `/api/player/control?requestId=…` | Historical request and actual permission |
| POST | `/api/player/control/manual` | Invalidate local permission and clean up owned work |
| POST | `/api/buildings/plans` | Store one building intent |
| GET | `/api/buildings/submission?requestId=…` | Read a building submission |
| POST | `/api/drafts/plans` | Store one temporary-draft intent |
| GET | `/api/drafts/submission?requestId=…` | Read a draft submission |

Mutations require JSON and the process token in `X-RimGovernor-Player`. The
read-only service exposes none of these player routes. Old building control
routes and the old control flag have no aliases.

Draft submission accepts exactly this shape:

```json
{
  "requestId": "player-request-id",
  "expected": {"colonyId": "colony", "loadToken": "load", "mapId": 0},
  "draft": {"pawnId": "exact-pawn-id"}
}
```

Its response echoes those fields and adds `planId`, `actionId` and `revision`
(a positive canonical decimal uint64 string). A newly stored request returns 201;
an exact replay or successful lookup returns 200. Request IDs share one namespace
across action families. Conflicting reuse returns 409. Submission never acquires
authority or drafts a pawn; native eligibility and CAS are resolved by fresh
executor inspection when a player enables the plan.

Manual retains the existing exact-world, direction-CAS and durable request
semantics. There is no dashboard-facing Acquire route: the dashboard never
independently acquires authority, it only submits commands through the plan
routes above for the bot to execute under its own Auto-mode authority.
`GET /api/player/control[?requestId=…]` still reports any Acquire-kind
control record that exists (for example historical evidence written some
other way), but nothing reachable from the dashboard can create a new one.
Historical results never confer current permission. Bodies
remain bounded to 8192 bytes, with duplicate, unknown, null, malformed and trailing
fields rejected. Errors retain the existing sanitized code/detail and control
record/state/error shapes. Missing lookup is not proof that a timed-out POST had
no effect; clients recover by reading the same request ID without automatic POSTs.

## Plan projection

`GET /api/plan?id=…` returns the existing plan envelope. Each action is one of:

- `kind: "building"` with `building` and no `draft` payload.
- `kind: "owned_draft"` with `draft: {"pawnId": "…"}` and no `building` payload.

Ordinary `progress` keeps its current fields. When draft cleanup evidence exists,
it also contains `draftCleanup: {"stage": "…"}`. The field is absent before draft
dispatch and for buildings. Stages are `awaiting_claim`, `not_acquired`, `required`,
`dispatched`, `uncertain`, `released` or `superseded`. Cleanup does not change an
already recorded ordinary outcome. The public projection omits claim IDs, leases,
native tokens and controller-session ownership data.

The Go player surface adds `SubmitDraft(context.Context,
store.DraftSubmissionRequest) (store.DraftSubmission, bool, error)` alongside
existing building submission and common Acquire/Manual/State methods. Its reader
adds `LookupDraftSubmission(context.Context, string) (store.DraftSubmission,
error)`. HTTP accepts these fixed capabilities; production service composition
supplies the same Player and store used by the worker.

## Dashboard behavior

Building and draft forms share token bootstrap, current permission, direction
CAS and Manual. Acquisition itself is not dashboard-initiated; the forms only
display whatever the bot's own Auto-mode authority currently reports. Manual
remains available regardless of that outcome. Background refresh preserves
both forms, request IDs and last-good data. Session/world changes exclude
stale permission and responses without silently resubmitting either intent.

Show ordinary progress and cleanup separately. Explain that a standalone temporary
draft releases its owned claim when the plan finishes; it is not a persistent draft
toggle. The UI has no general undraft, claim-adoption or native-token input.


## Native draft acceptance

The Python `scripts/native_go_draft_acceptance.py`/`container_scenario.py`
scenario this section once described (`GuardedConstructionFixture` draft
claim/release evidence for ordinary completion, player override, transport-error
recovery, HTTP response-body loss followed by lookup, and disabled
same-database restarts) was removed with the rest of the Python acceptance
toolchain in [G01.13](https://github.com/davidarcher/rimgovernor/issues/33);
equivalent Go coverage is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).

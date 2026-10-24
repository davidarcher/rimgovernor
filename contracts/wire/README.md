# Shared native protocol

This package defines the brain/native boundary as plain C# `sealed record`s in
`brain/src/RimGovernor.Brain.Contracts` (namespace `RimGovernor.Contracts.Wire`; one file per
family: `Authority.cs`, `Clock.cs`, `Common.cs`, `Defs.cs`, `Lifecycle.cs`, `Mirror.cs`,
`Observations.cs`, `Operations.cs`, `Placement.cs`, `Presentation.cs`, `Receipts.cs`). The brain is an
in-process library that calls the mod through `INativeFacade` without serialization; the records are
serialized to JSON only on the loopback HTTP channel (`/native/<tool>`, see
[http-channel](../../docs/developers/architecture/http-channel.md)) and in tests. Wire edits are made by
editing the records: there is no code generation, no field numbering and no compatibility ledger. Game
definitions remain open native identifiers, not enums of the current installed content.

Field names below are written in snake_case for readability; the records use PascalCase properties
and the JSON uses camelCase (`reach_budget_ms` is `ReachBudgetMs` / `reachBudgetMs`).

A `PawnState` row is read whole: every section (needs, health, equipment, biography, settings, social,
animal state, the DLC blocks) is present for every pawn, and the cases a section can lack are named
states rather than absent members: `not_animal`, `inactive` for an absent DLC, `none` for no care
policy, ideoligion or nearest colonist, `no_hostiles` for raid armor, `free` for a pawn that is not
a guest. `PawnSettings.work` is `never_works` for a pawn without work settings, `uninitialized` for
a working pawn whose tracker the game has not initialized, or `works` with `manualPriorities`
(numbered or checkbox mode) and the rows. `WorkSetting.priority` is native
effective priority: 0–4 in numbered mode and 0 or 3 in checkbox mode. Reads never initialize a work
tracker or grant permission to change settings.

Tend reachability is its own opt-in read, `TendDoctorsRequest` → `TendDoctorSnapshot`, keyed by pawn
id: per requested pawn the doctor-side gates the native tend order enforces (pawn-control
eligibility, drafter, spawned, WorkGiver_Tend's required capacities) plus `reach`, this pawn's
`CanReach(ClosestTouch, Deadly)` over the other requested pawns. Reachability is pairwise, so it runs
under the request's `reach_budget_ms` (the brain sends `FacadeNativeClient.PawnsTendReachBudgetMs`);
a doctor reached after it is spent carries `reach_skipped`, which is unknown, not "reaches nobody".
- [Validation](validation.md): shared presence, bounds and outcome requirements.
- [Observations](observations.md), [operations/receipts](operations.md),
  [clock/lifecycle](clock-lifecycle.md), [placement](placement.md)
  and [presentation](presentation.md): per-family contract semantics.

A compiling record is not evidence of native behavior.

## Wire and validation

`WireJson` (`Json/WireJson.cs`) is the one JSON entry point: source-generated System.Text.Json
(`WireJsonContext`), camelCase names, every member written (zeros and falses included), `required`
members enforced on read, enums as their wire-name strings (`WireEnumConverter`), unknown
members disallowed, choices as type-discriminated objects (the `...Case` records under each
`...Outcome`), and 64-bit counters as plain JSON numbers. Each native tool has one fixed request and
reply type. Read tools never include a mutation branch.

Unknown-member rejection is on. A request validator checks required presence, supported enum values,
collection limits and application invariants after parsing. A choice must be selected where the
contract requires a result or operation.

- A missing optional observation means unknown, never false or zero. Required
  context and request fields must be present even when their value is zero.
- A complete empty collection means known empty. Missing, failed, truncated or
  stale observations carry explicit unavailability or completeness information.
- Identifiers preserve exact case and contents. Opaque IDs must be nonblank UTF-8,
  contain no NUL and occupy at most 256 bytes. Definitions are resolved against
  current native definitions; labels do not establish identity.
- Generations, directions and attempts are 64-bit unsigned counters, serialized as JSON numbers.
  Attempt zero and an authority-acquiring direction zero are invalid.
- Native map IDs are nonnegative `int32`; zero is valid. Ticks are `int64` to match
  brain arithmetic, while observations retain the actual native tick value.
- Diagnostic text is bounded to 4096 Unicode scalar values. Truncating diagnostics
  must preserve valid text; truncating facts must never claim completeness.

## Identity and authority

Every observation identifies its colony, load and map, plus its observed tick.
The native generation is optional only when no authority observation is available.
An identity change invalidates pending reads, pages and commands. Page cursors are
opaque and scoped to the producing observation, never coordinate reservations.

The current native colony adapter projects `DevelopmentFacts.power`, conduit
`furniture` and per-network `networks`; omitted research remains unported,
not observed empty. Power rows carry refuelable service facts (`fuel`, `target_fuel`,
`out_of_fuel`, `allowed_fuel_defs`, `broken_down`) when the building has the
matching component, and batteries appear as rows with
`stored_watt_days`/`capacity_watt_days`. `networks` summarises each native
power net's generation, consumption, stored and capacity energy. Power building
references carry identity only, without operation-precondition snapshot tokens.
Network IDs group one observation and must not be retained as stable cross-load
identities. `FoodStock.room_id` names the native room holding an item at
observation time, so storage-temperature policy can join stock to room reads.

Authority is a mode (`MODE_AUTO` or `MODE_MANUAL`) plus a generation. There is
no negotiated lease, lease ID, session, owner, duration or expiry: time alone
never revokes authority, so there is nothing to acquire or renew. `Control.SetMode`
is produced by the explicit player-control path and `Auto` grants the bot
authority outright. Status does not grant authority. Native player orders,
explicit Manual and identity changes invalidate authority. An ordinary pause,
letter or logging pause is not Manual.

External `Control.Revoke` requests permit only `MANUAL`, `DISCONNECT` and
`SHUTDOWN`; other revocation reasons originate in native events.
Native raises `SHUTDOWN`
when the game ends in an orderly way (process exit, or a game unload such as
quit-to-menu or loading another save) while authority is Active. An ended
game's final authority stays readable: `authority_read_status` for its exact
identity answers `Inactive` with that final reason, generation and tick until
another game is loaded, after which the identity is stale as usual.
A successful `SetMode` or explicit revoke increments the expected generation by
exactly one, including revoking an already inactive authority. Overflow fails
closed. Native refresh applies context changes before the CAS check; a
mismatched generation fails rather than returning a grant at another
generation.

New commands validate identity, generation and ownership atomically on the
game thread. The attempt key is `(controller_session_id, action_id, attempt_id)`;
the brain namespace survives brain process restarts. Record an attempt
before effects. An identical admitted command replays its receipt, while conflicting
reuse is refused. Revocation does not erase receipts. A bounded ledger refuses new
admissions when full rather than evicting attempts that could still be retried.
The native ledger is unsaved and discarded on load; no saved-game migration applies.

Pre-admission failure guarantees no admitted effect. An admitted uncertain outcome
requires a correlated receipt and observation before any retry. A receipt proves
neither completed pawn work nor a durable outcome after a changed native identity.

## Limits

Shared [boundary validation](validation.md) and each family contract document
define required presence and semantic constraints beyond JSON parsing.
Control/observation replies carry no envelope size cap. Dedicated media replies permit
up to 32 MiB of image bytes within a 48 MiB JSON envelope, as specified by the
presentation contract; this exception never applies to arbitrary data payloads.

Shared trade target arms and acquisition/packing facts: [controllable trade](../../docs/developers/contracts/controllable-trade.md).

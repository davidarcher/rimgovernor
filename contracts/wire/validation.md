# Boundary validation rules

System.Text.Json parsing (`WireJson`) is the first step. Deserialized records remain
transport values until the owning adapter applies these rules and its family contract
document. These requirements are shared by the brain and the native mod; round-trip
tests intentionally include shapes that are invalid game requests.

## Shared rules

Reject unknown JSON members, invalid Unicode, unsupported enum values (enums are wire-name
strings), missing required choices and nonfinite numeric game values. JSON null
leaves a member absent; it cannot satisfy required presence.

| Value | Required presence and bounds |
| --- | --- |
| Identity | colony_id, load_token, map_id; map>=0 including0. map_id names which loaded map a read or operation is scoped to; it need not be the viewed map. A complete identity whose map is no longer loaded is STALE_IDENTITY with the viewed map's context as observed_context. Presentation state that lives on the viewed map (selection, camera, watches) additionally requires map_id to be the viewed map, else STALE_IDENTITY with the viewed context. Authority is granted for the viewed map: a view change invalidates it as IDENTITY_CHANGED, one generation per change. |
| ObservationContext | identity and nonnegative int64 tick; positive native_generation whenever authority is available. |
| Owner | nonblank controller_session_id and positive uint64 player_direction. |
| AttemptKey | controller_session_id, action_id and positive uint64 attempt_id. |
| WritePrecondition | identity, positive expected_generation and complete attempt. |
| Cell | both coordinates, each within the relevant native map; no missing-to-zero conversion. |
| Opaque ID/definition/token | valid nonblank Unicode, <=256 UTF-8 bytes, no NUL; exact case/content; narrower native input limits are documented in the [presentation contract](presentation.md). |
| Failure/Unavailable | supported nonzero reason/code; bounded diagnostic if supplied; absence of observed context never fabricates a loaded identity. |
| Diagnostic | <=4096 Unicode scalar values; only diagnostic text may be truncated at scalar boundaries. |
| Collection | explicit known-complete versus partial/unavailable evidence; no silent truncation. Family limits and queries define completeness. |

A choice record makes alternatives mutually exclusive but a deserialized request may
still carry none. Every request operation selector and reply outcome selector must be
selected. Nested result/availability alternatives must be selected whenever their
parent section is included. In observation rows, missing optional scalars remain
unknown; do not reject truthful incomplete observations as though all facts were
required. Requested missing sections need explicit unavailability/read issues.

## Method and result obligations

| Family | Required request and reply checks |
| --- | --- |
| Authority | ReadStatus requires identity. SetMode requires identity, expected generation and a mode (Auto or Manual); Revoke requires identity/generation and an allowed external revocation reason (Manual, Disconnect or Shutdown). There is no lease, duration or renewal. Granted must be active; revoked must be inactive. Caller direction never authenticates itself. |
| Placement | Exact identity, 1–16 candidates with def/x/z/rotation. Stuff optional/default. One ordered result per candidate, actual batch context, complete cost/footprint/blocker facts or candidate failure. Cardinal output rotations only. See the [placement contract](placement.md) for bounds/material availability. |
| Observation reads | Exact expected identity; optional query filters/details and pagination have family-defined defaults. Reply context is actual, allowing ticks/generation to advance. Each SnapshotRef includes context/entity/token. Complete page has no next cursor and no unreadable required facts. Unavailable nested sections do not poison independently known siblings. |
| Guarded operations | Complete WritePrecondition and one command. Every EntityPrecondition requires exact entity ID and expected snapshot token; standalone expected_* tokens are required. Exact target/cell/definition references for the chosen command are required. Optional patch fields mean unchanged; provided assignments select set or clear. Empty patch is no-change, never an implicit reset. |
| Policy/bills/zones | Policy replacement wrappers are all required; empty explicitly clears. Schedule, if supplied, has24 entries. Bill operations resolve exact bill under bench stack token. Zone rectangles have positive dimensions with checked expansion<=4096cells. Filters distinguish absent patch from present-empty replacement. |
| Clock | `Run` requires current authority, one ordinary speed, a complete watch policy and the planned hold serial (a resume after a newer hold entry is refused); it has no attempt key, tick budget or deadline and is refused while a game-forced pause is active. `Hold` requires the loaded identity and a reason, never current authority. `Heartbeat` requires the loaded identity. Event reads never acquire control and never count as contact. |
| Receipt/progress | Complete attempt/admission context/original owner; selected outcome and family-correlated evidence. Read progress carries actual context and causal-after-dispatch inspection; complete/absent/unsuccessful require complete inspection. No fabricated effect IDs in previews. |
| Lifecycle | Save requires exact player context, save name and expected tick with actual pause. Load requires instance/current direction/request ID/name/readiness/deadline and expected player context when replacing a map. ReadLoad/ReadSave require request ID and instance. Completion never means authority restoration. |
| Player presentation | Current controller identity (a reply for a changed world is refused); the only lease is the render-demand lease, 1..30 real seconds. No token or per-player trust is checked at this layer. See the [presentation contract](presentation.md) for exact native limits. |

Patch and optional-read defaults must be implemented deliberately: absent boolean
patches do not mean false, absent available counts do not mean zero, omitted
policy replacements do not mean clear, and a missing selection does not mean an
empty selected set. A reply with inconsistent completeness, mismatched command
evidence, impossible choice/context correlation or unsupported enum is invalid
evidence; retain prior known data and reconcile instead of acting on it.

Native validators additionally resolve definitions, eligibility, settings,
resources, reachability, reservations, current player changes and other ordinary
game rules on the game thread. Those stateful checks cannot be established by the
record types or by a successful serialization test.

## New colony scenario parts

`ValidateNewColonySpec` (`brain/src/RimGovernor.Brain.Bridge.Contracts/Foundation/NewColonyValidation.cs`) bounds the parts on
`NewColonySpec`: at most 64 entries per kind; `colonist_count` 1-10; starting-thing
placement set, `count` 1-100000, `allow_roofed` only on scattered things; forced-trait
`degree` -10..10; forced-hediff `severity` 0-1; `chance` and need `level` 0-1 and finite;
a pawn `context` set on every pawn-modifying part; condition, building and incident lists
non-empty, unique identifiers; `player_faction` and `arrival_method` supported cases. An
absent required number is a refusal, not zero. Whether a defName exists is native's call.

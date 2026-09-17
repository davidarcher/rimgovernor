# Boundary validation rules

Official Protobuf parsing is the first step. Generated objects remain transport
values until the owning adapter applies these rules and its family coverage
document. These requirements are shared by both implementations; compiler
roundtrip fixtures intentionally include shapes that are invalid game requests.

## Shared rules

Reject unknown ProtoJSON fields, invalid Unicode, unsupported numeric enum values,
missing required oneofs and nonfinite numeric game values. Standard ProtoJSON null
leaves a field absent; it cannot satisfy required presence. Standard numeric
spellings accepted by the official parser need no additional lexical parser.
Binary is a test/possible future transport; admitting binary requests additionally
requires recursive unknown-field rejection. Current MCP uses ProtoJSON.

| Value | Required presence and bounds |
| --- | --- |
| Identity | colony_id, load_token, map_id; map>=0 including0. map_id names which loaded map a read or operation is scoped to; it need not be the viewed map. A complete identity whose map is no longer loaded is STALE_IDENTITY with the viewed map's context as observed_context. Presentation state that lives on the viewed map (selection, camera, capture, watches) additionally requires map_id to be the viewed map, else STALE_IDENTITY with the viewed context. Authority is granted for the viewed map: a view change invalidates it as IDENTITY_CHANGED, one generation per change. |
| ObservationContext | identity and nonnegative int64 tick; positive native_generation whenever authority is available. |
| Owner | nonblank controller_session_id and positive uint64 player_direction. |
| AttemptKey | controller_session_id, action_id and positive uint64 attempt_id. |
| WritePrecondition | identity, positive expected_generation, nonblank lease_id and complete attempt. |
| Cell | both coordinates, each within the relevant native map; no missing-to-zero conversion. |
| Opaque ID/definition/token | valid nonblank Unicode, <=256 UTF-8 bytes, no NUL; exact case/content; narrower native input limits are documented in presentation coverage. |
| Failure/Unavailable | supported nonzero reason/code; bounded diagnostic if supplied; absence of observed context never fabricates a loaded identity. |
| Diagnostic | <=4096 Unicode scalar values; only diagnostic text may be truncated at scalar boundaries. |
| Collection | explicit known-complete versus partial/unavailable evidence; no silent truncation. Family limits and queries define completeness. |

`oneof` makes alternatives mutually exclusive but does not require one to be
selected. Every request operation selector and reply outcome selector must be
selected. Nested result/availability alternatives must be selected whenever their
parent section is included. In observation rows, missing optional scalars remain
unknown; do not reject truthful incomplete observations as though all facts were
required. Requested missing sections need explicit unavailability/read issues.

## Method and result obligations

| Family | Required request and reply checks |
| --- | --- |
| Authority | ReadStatus requires identity. Acquire requires identity, expected generation, Owner and duration; Renew requires identity/generation/session/lease/duration; Revoke requires identity/generation and an allowed external revocation reason. Duration1000–30000ms. Granted must be active for the requested owner; revoked must be inactive. Caller direction never authenticates itself. |
| Placement | Exact identity, 1–16 candidates with def/x/z/rotation. Stuff optional/default. One ordered result per candidate, actual batch context, complete cost/footprint/blocker facts or candidate failure. Cardinal output rotations only. See placement coverage for bounds/material availability. |
| Observation reads | Exact expected identity; optional query filters/details and pagination have family-defined defaults. Reply context is actual, allowing ticks/generation to advance. Each SnapshotRef includes context/entity/token. Complete page has no next cursor and no unreadable required facts. Unavailable nested sections do not poison independently known siblings. |
| Guarded operations | Complete WritePrecondition and one command. Every EntityPrecondition requires exact entity ID and expected snapshot token; standalone expected_* tokens are required. Exact target/cell/definition references for the chosen command are required. Optional patch fields mean unchanged; provided assignments select set or clear. Empty patch is no-change, never an implicit reset. |
| Policy/bills/zones | Policy replacement wrappers are all required; empty explicitly clears. Schedule, if supplied, has24 entries. Bill operations resolve exact bill under bench stack token. Zone rectangles have positive dimensions with checked expansion<=4096cells. Filters distinguish absent patch from present-empty replacement. |
| Clock | Start requires complete authority, speed, policy, duration and positive bounded tick budget. Renew/speed require complete original epoch identity/owner and current authority. Pause requires exact identity/epoch owner and cannot target a replacement. Applied/uncertain receipts preserve attempt and admission context. ReadAttempt never acquires control. |
| Draft cleanup | Exact identity, pawn snapshot, original owner and claim ID. Release only unchanged owned claim; uncertainty is explicit and full normal ledger cannot prevent cleanup. Already-released refers to that same claim. |
| Receipt/progress | Complete attempt/admission context/original owner; selected outcome and family-correlated evidence. Read progress carries actual context and causal-after-dispatch inspection; complete/absent/unsuccessful require complete inspection. No fabricated effect IDs in previews. |
| Lifecycle | Save requires exact player context, save name and expected tick with actual pause. Load requires instance/current direction/request ID/name/readiness/deadline and expected player context when replacing a map. ReadLoad/ReadSave require request ID and instance. Completion never means authority restoration. |
| Player presentation/input | Current trusted player identity/lease and exact server-retained capture for commands. Sequence/frame/scene/selection/window checks apply before effect. Proven refusal and possible-effect uncertainty are distinct. Media has separately bounded bytes/frames/acks. See presentation coverage for exact native input limits. |

Patch and optional-read defaults must be implemented deliberately: absent boolean
patches do not mean false, absent available counts do not mean zero, omitted
policy replacements do not mean clear, and a missing selection does not mean an
empty selected set. A reply with inconsistent completeness, mismatched command
evidence, impossible oneof/context correlation or unsupported enum is invalid
evidence; retain prior known data and reconcile instead of acting on it.

Native validators additionally resolve definitions, eligibility, settings,
resources, reachability, reservations, current player changes and other ordinary
game rules on the game thread. Those stateful checks cannot be established by a
schema or by a successful serialization test.

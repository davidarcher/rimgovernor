# Shared native protocol

This package defines the Go/native boundary with Protocol Buffers. Official
`protoc` and language plugins generate the bindings. There is no repository-owned
schema compiler. Game definitions remain open native identifiers, not generated
enums of the current installed content.

`PawnDetails.work` requests only the work portion of `PawnSettings`, independently
of full settings. `work_applies` distinguishes inapplicable pawns from unreadable
work tables; `manual_work_priorities` distinguishes numbered and checkbox mode.
`WorkSetting.priority` is native effective priority: 0–4 in numbered mode and
0 or 3 in checkbox mode. Missing values remain unknown. Reads never initialize a
work tracker or grant permission to change settings.

- [Boundary inventory](coverage.md): all97 current boundary rows and55 native exports.
- [Fixed MCP tools](mcp-tools.md):78 descriptor methods, exact wrappers and capabilities.
- [Validation](validation.md): shared presence, bounds and outcome requirements.
- [Observations](observation-coverage.md), [operations/receipts](operation-coverage.md),
  [clock/lifecycle](clock-lifecycle-coverage.md), [placement](placement-coverage.md)
  and [presentation](presentation-coverage.md): source facts and owning contracts.

Complete the message families and source/consumer coverage before implementing
the Go and native adapters. A compiled schema is not evidence of native behavior.
The unfinished contract inventory and implementation gates remain in
[N01](../../docs/BACKLOG.md#n01--unified-rimgovernor-native-mod).

## Wire and validation

Use the official ProtoJSON parser and formatter at the MCP boundary. The outer
SDK request contains one `request` string; the response contains one `payload`
string plus SDK operation metadata. The strings contain the generated message's
ProtoJSON. SDK reflection must not serialize generated message properties itself.
Each advertised capability has one fixed request and reply type. Read capabilities
never include a mutation branch. Service descriptors identify typed operations;
they do not require a gRPC transport or a second game server.

Default unknown-field rejection remains enabled for ProtoJSON. A request validator
checks required presence, supported enum values, collection limits and application
invariants after parsing. It does not reproduce another parser's numeric spelling,
whitespace classification or historical serialization quirks. Generated `oneof`
cases must be selected where the contract requires a result or operation.

- A missing optional observation means unknown, never false or zero. Required
  context and request fields must be present even when their value is zero.
- A complete empty collection means known empty. Missing, failed, truncated or
  stale observations carry explicit unavailability or completeness information.
- Identifiers preserve exact case and contents. Opaque IDs must be nonblank UTF-8,
  contain no NUL and occupy at most 256 bytes. Definitions are resolved against
  current native definitions; labels do not establish identity.
- Generations, directions and attempts use `uint64`. Official ProtoJSON renders
  these as decimal strings without changing their generated numeric type.
  Attempt zero and an authority-acquiring direction zero are invalid.
- Native map IDs are nonnegative `int32`; zero is valid. Ticks are `int64` to match
  controller arithmetic, while observations retain the actual native tick value.
- Diagnostic text is bounded to 4096 Unicode scalar values. Truncating diagnostics
  must preserve valid text; truncating facts must never claim completeness.

## Identity and authority

Every observation identifies its colony, load and map, plus its observed tick.
The native generation is optional only when no authority observation is available.
An identity change invalidates pending reads, pages and commands. Page cursors are
opaque and scoped to the producing observation, never coordinate reservations.

Authority acquisition is produced by the explicit player-control path. Status
does not acquire authority, and renewal cannot resurrect an expired or revoked
lease. Native player orders, explicit Manual, lease expiry and identity changes
invalidate authority. An ordinary pause, letter or logging pause is not Manual.
Acquisition and renewal require a duration between 1000 and 30000 milliseconds;
expiry uses monotonic real time rather than game ticks.

External `Control.Revoke` requests permit only `MANUAL`, `PLAYER_DIRECTION`,
`DISCONNECT`, and `SHUTDOWN`; other revocation reasons originate in native events.
A successful acquire or explicit revoke increments the expected generation by
exactly one, including revoking an already inactive authority. Renewal preserves
the generation, lease ID, session and original player direction. Overflow fails
closed. A granted reply has positive remaining lease time no greater than the
requested duration. Native refresh applies expiry/context changes before the CAS
check; a mismatched generation fails rather than returning a grant at another
generation.

New commands validate identity, generation, lease and ownership atomically on the
game thread. The attempt key is `(controller_session_id, action_id, attempt_id)`;
the controller namespace survives controller process restarts. Record an attempt
before effects. An identical admitted command replays its receipt, while conflicting
reuse is refused. Revocation does not erase receipts. A bounded ledger refuses new
admissions when full rather than evicting attempts that could still be retried.
The native ledger is unsaved and discarded on load; no saved-game migration applies.

Pre-admission failure guarantees no admitted effect. An admitted uncertain outcome
requires a correlated receipt and observation before any retry. A receipt proves
neither completed pawn work nor a durable outcome after a changed native identity.

## Handoff checks

Before adapter implementation, verify every in-scope source/consumer field is
represented or explicitly excluded, every schema compiles with the pinned official
tools, and C#/Go exchange the same messages through both binary and ProtoJSON.
Exercise zero versus missing, oneof variants, integer extremes, invalid inputs,
known-empty versus unavailable observations and application-limit refusals.
After the contract handoff, native adapters still require fresh-game acceptance,
player-override and uncertain-write checks, and observed outcomes for each family.

Shared [boundary validation](validation.md) and each family coverage document
define required presence and semantic constraints beyond official parsing.
Control/observation replies are bounded to1MiB. Dedicated media replies permit
up to32MiB of image bytes within a48MiB ProtoJSON envelope, as specified by the
presentation contract; this exception never applies to arbitrary data payloads.

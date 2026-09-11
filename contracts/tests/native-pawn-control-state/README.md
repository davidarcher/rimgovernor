# Pawn snapshot and draft claim state

Build the locked net472 test project. Run its executable with private Bridge DLL,
Runtime directory, SDK directory, game managed directory and Harmony directory.
The tests exercise the compiled record implementation using uninitialized native
identity references; they do not create game state or install native hooks.
Private package compilation checks the actual game/Harmony/SDK APIs separately.

`NativePawnControlState.Initialize()` must run explicitly during capability
registration or lifecycle initialization. Typed observations only verify live exact
hook membership; they never install hooks or allocate native authority. There is
no support advertisement in this foundation slice.

`Observe`/`Check` produce stable opaque native snapshot tokens for exact pawn and
colony/load/map identity, context transitions, draft setter and ordered-job epochs,
position, faction, native draft eligibility, current job and queued job targets.
At most4096 pawn records per unsaved Game and256 queued jobs/target entries are
supported; overflow is explicit. This is a draft-control CAS, not a health/settings
snapshot or permission to issue arbitrary orders.

PrepareClaim requires an exact snapshot, an undrafted eligible pawn and the live
owned authority scope with exact original session/direction. The caller rechecks
admission immediately before invoking the ordinary draft setter. CompleteClaim
requires exactly one observed false-to-true transition. It can retain the causal
cleanup claim after lease expiry without granting authority. Every later setter,
including an undraft/redraft between reads, invalidates that claim. Successful
external ordered jobs invalidate claims; same-owner admitted orders preserve them.
Ordinary simulation job progress updates the snapshot without discarding ownership.

PrepareRelease needs exact identity, original token, claim ID and original typed
owner. It does not require a current lease. The caller invokes the ordinary setter;
CompleteRelease records success only after exact undrafted readback. An exception
or unavailable read leaves a correlated uncertain ticket. No rollback or implicit
retry occurs. Caller diagnostics may remain uncertain even after a verified effect.
The separate latest-release ticket permits exact request replay before old-token
CAS, only while post-release facts remain unchanged. New claims, later setters,
orders and context transitions prevent obsolete replay. It consumes no ordinary
operation-ledger capacity. Legacy string draft ownership remains separate.

Integration acceptance must prove actual hook installation/invalidation, canonical
SetDrafted admission, manual/expired cleanup, exact release replay and uncertainty
through real native callers. This foundation does not advertise those operations.

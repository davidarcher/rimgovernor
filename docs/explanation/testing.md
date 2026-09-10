# Testing and evidence

[Documentation](../README.md) · [System overview](overview.md)

A test result is useful when its claim is precise. RimBot has fixture tests, native game
probes, local-model checks and sustained campaigns. They observe different boundaries,
so success in one does not automatically establish another.

## Fixtures answer questions about controller behavior

Python tests can supply a known observation, change a revision or simulate a lost
receipt, then verify the controller's response. Dashboard tests can reproduce refresh
failures and draft preservation without starting the game. These tests make difficult
boundary cases cheap to repeat.

Their limitation is the supplied world. A native-shaped fixture can verify that an
algorithm rejects a blocked doorway, but it cannot show that an actual pawn walked
through a room. The distinction is about the evidence, not test quality.

## Native scenarios observe ordinary game outcomes

A native scenario runs the controller and RimWorld together. It can issue an order,
advance supervised simulation, then inspect buildings, pawn jobs, inventory or health. A
construction scenario should wait for the completed native building; a production
scenario should observe real output and consumption.

Docker supports this full path. The current `container_native_acceptance.py` primarily
asserts startup, independent clocks, cleanup, peer survival and paired checkpoint
retention, with optional rendering/player-input checks. More pawn-work scenarios require
additional assertions or portable versions of existing probes. There is no Docker
boundary preventing those observations.

## Interpretation and execution are separate questions

Repeated native cases can reuse one owned game while restoring their baseline and
creating fresh controller state. This saves engine/mod initialization but keeps
process-wide mod state and Unity caches. The opt-in execution-suite mode verifies
native load identity, pause, ownership cleanup and revoked prior clients before
continuing; a failed boundary retires the worker. Fresh-process checks remain necessary
for startup, crash recovery and static-state isolation. See
[reusable execution cases](../how-to/headless-probes.md#reuse-one-game-between-execution-cases).

A scripted semantic request can establish that admission and Hands perform the right
native action. It does not establish that a model reliably interprets many ways a player
might phrase that request. Conversely, a fixed-fact model benchmark can score the
interpreted request without proving any pawn work occurred.

Campaigns add time, repeated trials and varying colony conditions. Their manifests
matter because a changed source revision, baseline, model or objective changes the
experiment. A short passing streak describes those trials, not an unrestricted survival
guarantee. Failed attempts remain part of the evidence.

## Recording closes part of the feedback loop

Existing SQLite events, action archives, diagnostics and native logs help explain
failures. Input hashes and retained checkpoints help reproduce their context. These
components are not yet a complete flight recorder: some observations are temporary, some
payloads are bounded, and the diagnostics API exposes a recent window rather than the
whole run.

The desired loop is a named scenario, an automatic failure bundle, offline inspection, a
focused regression and another run of the same native assertion. That reusable runner
and recorder work is tracked in [B17](../BACKLOG.md). Treat it as planned work; do not
assume a current failure bundle contains every call or that a checkpoint can replay the
simulation bit for bit.

Use [choose checks](../how-to/choose-tests.md) when working on a change, [diagnostic
reference](../reference/diagnostics.md) to see what is recorded today, and [inspect a
failed run](../how-to/inspect-failure.md) to follow the current evidence.

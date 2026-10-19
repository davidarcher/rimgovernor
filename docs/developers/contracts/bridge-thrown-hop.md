# What a thrown game-thread hop does

[Contracts](README.md) · [Developer guide](../README.md)

When the body of a game-thread hop (`ProtoBoundary.OnGameThread`) throws, only that
call fails. The game thread, the admission queue and later calls are untouched,
so **a rethrow is safe**: no catch is needed to protect the mod.

## Path of a throw

1. `MainThreadAdmission.Run` catches it and fails the hop's completion
   (`TrySetException`); the host's pump and the game thread never see it.
   `OnGameThread`'s `finally` still closes the watchdog entry.
2. The awaiting `InProcessNativeFacade` method rethrows the original exception
   to its caller, the brain (no `TargetInvocationException`: nothing is invoked
   by reflection).
3. On the test-only `/native/{op}` route ([HTTP channel](../architecture/http-channel.md))
   the exception becomes a 500 `{"code":"tool_failed","detail":"<message>"}`; a
   request body the wire records refuse is a 400 `invalid_request` before any hop.

## Rules

- A read may rethrow instead of returning a default. Prefer a typed
  `Failure`/`Unavailable` reply where the caller can act on it; rethrow for bugs.
  A rethrow loses the typed reply, so never rethrow for expected game states.
- A frame section that throws fails the frame. The snapshot stream is not a
  facade hop, so a rethrow would only skip the frame and leave the brain on a stale one. Native
  publishes a frame carrying only `BundleSnapshot.failure` (`NATIVE_FAILURE`, detail
  `snapshot section <name>: <ExceptionType>: <message>`) for `colonyFacts`,
  `population`, `research` and `pawns`; `Frames.cs` turns it into a
  `*Refusal` (tool `snapshot_frame`) naming the section.
- A catch stays only where the game itself throws on legitimate input; give it a
  one-line reason.
- Not covered: exceptions outside a hop (Harmony hooks such as
  `ObservationFrameHook`/`TimeControlsRow`, tick handlers). They run on the game
  thread outside any call, so none of the above applies.

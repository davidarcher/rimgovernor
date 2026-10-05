# How the RimBridge host treats a thrown hop

[Contracts](README.md) · [Developer guide](../README.md)

When a bridge hop (the body handed to `ProtoBoundary.OnMainThread`/`RunHop`)
throws, the host replies with an ordinary tool result carrying `success: false`.
The connection, the game thread and later calls are untouched, so **a rethrow is
safe**: no catch is needed to protect the bridge. Verified by decompiling
the upstream `RimBridgeServer.dll`/`RimBridgeServer.Core.dll` (now `RimGovernor.Host.dll`/`RimGovernor.Host.Core.dll`, vendored source; `ilspycmd`) and by a scratch throw
in a hop body on the running game (the call after it answered normally).

## Path of a throw

1. `MainThreadAdmission.Run` catches it and fails the hop's completion
   (`TrySetException`); the host's pump and the game thread never see it.
   `RunHop`'s `finally` still closes the watchdog entry and the frame account.
2. The tool method's `await` rethrows the original exception (tool methods are
   async, so there is no `TargetInvocationException`).
3. `AnnotatedExtensionCapabilityProvider` runs the method under
   `OperationRunner.RunAsync`, which returns an `OperationEnvelope`:

   | Exception | Status | Error code |
   | --- | --- | --- |
   | any `Exception` | Failed | `capability.failed` |
   | `OperationCanceledException` | Cancelled | `capability.cancelled` |
   | `TimeoutException` | TimedOut | `capability.timed_out` |
   | `MissingMethodException`/`TypeLoadException` naming `RimGovernor.Host.Sdk` | | `capability.sdk_mismatch` |

4. `LegacyToolExecution.InvokeAlias` (the registered GABP handler) never throws
   for a failed envelope; it composes the reply below.

## The reply

A normal GABP tool result (`isError` false on the wire):

```
{"success": false,
 "message":   "<exception.Message>",
 "exception": "<exception.ToString(), full managed stack trace>",
 "operation": {"OperationId": "op_...", "CapabilityId": "...", "Status": 3,
               "Success": false, "DurationMs": 18, "Result": null,
               "Error": {"Code": "capability.failed",
                         "Message": "<exception.Message>",
                         "ExceptionType": "System.InvalidOperationException",
                         "Details": "<exception.ToString()>"}}}
```

A completed hop carries `Status: 2`, `Success: true` and the tool's own
`payload`/`proto` field; a thrown hop has no `payload`, `proto`, `slot` or
`timing`. `bridge.decodeReceipt` treats `success: false` as a `*Refusal` with
`Cause` lifted from the first line of `exception`
(`System.InvalidOperationException: <message>`), so a throw surfaces as a named
refusal, never as empty data. `capability.failed` and `ExceptionType` appear only
in the structured receipt.

## Rules

- A read may rethrow instead of returning a default. Prefer a typed
  `Failure`/`Unavailable` reply where the caller can act on it; rethrow for bugs.
  A rethrow loses the typed reply and the stack trace rides in every such reply,
  so never rethrow for expected game states.
- A frame section that throws fails the frame. The snapshot stream is not a tool
  hop, so a rethrow would only skip the frame and leave Go on a stale one. Native
  publishes a frame carrying only `BundleSnapshot.failure` (`NATIVE_FAILURE`, detail
  `snapshot section <name>: <ExceptionType>: <message>`) for `colonyFacts`,
  `population`, `research` and `pawns`; `bridge/frames.go` turns it into a
  `*Refusal` (tool `snapshot_frame`) naming the section.
- A catch stays only where the game itself throws on legitimate input; give it a
  one-line reason.
- Not covered: exceptions outside a tool call (Harmony hooks such as
  `ObservationFrameHook`/`PlayerSpeedHook`, tick handlers). They run on the game
  thread outside any tool call, so none of the above applies.

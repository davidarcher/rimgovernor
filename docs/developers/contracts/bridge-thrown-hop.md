# How the RimBridge host treats a thrown hop

[Contracts](README.md) · [Developer guide](../README.md)

Finding for #1887 (epic #1668, fail-loudly). Question: when a bridge hop
(the body handed to `ProtoBoundary.OnMainThread`/`RunHop`) throws, does the
host reply with an error, drop the connection, or take the bridge down?

**Answer: a rethrow is safe.** The host turns it into an ordinary tool reply
with `success: false`; the connection, the game thread and later calls are
untouched. No catch is needed to protect the bridge, so a swallow whose only
reason is "do not break the hop" has no reason left.

## Evidence

Decompiled `RimBridgeServer.dll`/`RimBridgeServer.Core.dll` with `ilspycmd`,
then confirmed on the running game: a scratch throw in the
`rimgovernor/clock_read_status` hop body (not committed), called over the
GABP connection three times (normal, throwing, normal). The throwing call
returned in 43 ms, the third call answered normally in 3 ms, and the
connection stayed up. The game log carried no line for the throw.

## Path of a throw

1. `MainThreadAdmission.Run` catches it and fails the hop's completion
   (`TrySetException`); a thrown body never reaches the host's pump, so the
   game thread never sees the exception (the host's own pumps also catch and
   `Log.Error` anything that escapes). `RunHop`'s `finally` still closes the
   watchdog entry and the frame account.
2. The tool method's `await` rethrows the original exception (not a
   `TargetInvocationException`: tool methods are async, so `method.Invoke`
   returns a faulted task).
3. `AnnotatedExtensionCapabilityProvider` runs the method under
   `OperationRunner.RunAsync`, which catches `Exception` and returns an
   `OperationEnvelope` with status Failed, error code `capability.failed`.
   `OperationCanceledException` maps to Cancelled (`capability.cancelled`),
   `TimeoutException` to TimedOut (`capability.timed_out`), and a
   `MissingMethodException`/`TypeLoadException` naming `RimBridgeServer.Sdk`
   to `capability.sdk_mismatch`.
4. `LegacyToolExecution.InvokeAlias` (the registered GABP handler) never
   throws for a failed envelope; it composes the reply below.

## The reply

A normal GABP tool result (`isError` false on the wire), a JSON object:

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

A hop that completes carries `Status: 2`, `Success: true` and the tool's own
`payload`/`proto` field; a thrown hop has no `payload`, `proto`, `slot` or
`timing` block, so a caller that reads those fields sees them absent.

On the Go side `bridge.decodeReceipt` already treats `success: false` as a
`*Refusal`, with `Cause` lifted from the `exception` field's first line
(`System.InvalidOperationException: <message>`), so a thrown hop surfaces as a
named refusal with the exception type and message, never as empty data. The
`capability.failed` code and `ExceptionType` are only in the structured
receipt.

## What this means for #1668

- A read may rethrow instead of returning a default. Prefer a typed
  `Failure`/`Unavailable` reply where the caller can act on it; rethrow where
  the failure is a bug and the named refusal is the right signal.
- A rethrow costs the caller the typed reply (no `Failure` message, only the
  exception text), and the stack trace rides in every such reply, so keep
  rethrow for faults, not for expected game states.
- A catch stays only where the game itself throws on legitimate input; give
  it a one-line reason.
- Not covered: exceptions outside a tool call (Harmony hooks such as
  `ObservationFrameHook`/`PlayerSpeedHook` patch bodies, tick handlers). Not
  probed; they run on the game thread outside any tool call, so the findings
  above do not apply to them.

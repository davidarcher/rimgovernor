# Native attempt ledger checks

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-attempt-ledger
```

This probe is part of the consolidated `NativeContractProbes.csproj`. It links
production `NativeAttemptLedger` and canonical official generated messages, has
no game/SDK access and executes no native mutations.

One unsaved ledger belongs to one colony/load across maps and admits clock
control only (Operations/Execute is gone, #990). `InspectClock`/`AdmitClock`
accept only official StartRequest, RenewRequest and SpeedRequest messages with
their exact RPC names. Call `InspectClock` before rechecking a changed lease:
replay returns prior evidence, not new permission. If New, validate current
guards and call `AdmitClock` immediately before effects on the same game
thread; a refused guard consumes no entry. `FinishClockApplied` requires
explicit observed status; `FinishClockUncertain` permits missing evidence.
Status evidence must carry the same identity and a tick at or after admission.
Clock ReadAttempt has no in-flight branch, so `LookupClock` returns transient
correlated uncertainty until finalization. `Lookup` (receipts) reports a clock
attempt as AttemptConflict and anything else as unknown.

The descriptor-based comparer supplements generated C# equality, which does not
compare every optional scalar's presence. It compares typed fields with presence
and repeated order, not serialized bytes. Unknown binary fields are refused
using the official discard-unknown parser. Tests cover exact replay/conflict,
defensive copies, thread ownership, epoch/policy equality, method/type
mismatches and all 4096 slots.

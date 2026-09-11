# Native attempt ledger checks

```powershell
dotnet build contracts/tests/native-attempt-ledger/NativeAttemptLedgerTests.csproj -c Release -p:RestoreLockedMode=true
./contracts/tests/native-attempt-ledger/bin/Release/net472/NativeAttemptLedgerTests.exe
```

The test links production `NativeAttemptLedger` and canonical official generated
messages. It has no game/SDK access and executes no native mutations. Assertions
cover exact replay/conflict, optional presence (including nested/repeated zero and
false), original preconditions, oneof/list order, defensive copies, correlation,
pre-admission refusal, admitted uncertainty, thread ownership and all 4096 slots.

One unsaved ledger belongs to one colony/load across maps. The native adapter
owns its lifetime, current identity/authority/semantic guards and effect readback.
Call `Inspect` before rechecking a changed lease: replay returns prior evidence,
not new permission. If New, validate current guards and call `Admit` immediately
before effects on the same game thread. A refused guard consumes no entry.
The admitted opaque handle can finish as Applied, NoChange or Uncertain. There is
no admitted Failure or receipt overwrite; later progress is a separate read.

`Inspect` returns a transient correlated Uncertain execute reply for InFlight,
without finalizing the entry. `Lookup` continues to expose InFlight until finished.
An exception/readback loss after admission must finish Uncertain with only known
evidence; never retry dispatch. Unknown lookup is not absence or retry permission.
Receipts preserve admission context/owner after current generation changes.

The descriptor-based comparer supplements generated C# equality, which does not
compare every optional scalar's presence. It compares actual typed fields with
presence and repeated order, not serialized byte equality. Unknown binary fields
are refused using the official discard-unknown parser; strict ProtoJSON already
refuses unknown field names at the external boundary. Per-operation enum/value
legality and observation completeness remain the adapter's responsibility.

Clock `InspectClock`/`AdmitClock` accept only official StartRequest, RenewRequest
and SpeedRequest messages with their exact RPC names. These share the same 4096
entries with operations; cross-family key reuse conflicts, and wrong-family
lookups report AttemptConflict. `FinishClockApplied` requires explicit observed
status; `FinishClockUncertain` permits missing evidence. Status evidence must
carry the same identity and a tick at or after admission. Clock ReadAttempt has
no in-flight branch, so `LookupClock` returns transient correlated uncertainty
until finalization. Tests cover both families filling one ledger, immutable clock
replay after generation changes, exact epoch/policy equality and method/type
mismatches. This ledger does not itself enable clock execution.

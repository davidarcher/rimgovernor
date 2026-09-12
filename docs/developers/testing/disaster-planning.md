# Verify Go disaster planning

[Documentation](../../README.md) · [Contract](../contracts/disaster-planning.md)

Use the standard [scenario launcher](scenario-launcher.md) with fresh private Linux
inputs and a task-specific image/output directory. Build the native companion with
`DisasterFixture` and the routine acceptance fixtures. Stage the matching Linux Go
binary in the GABS input directory, then run:

```text
python scripts/native_go_routine_acceptance.py --root /worker/run --go-binary /inputs/gabs/rimgovernor-go --disaster-review
```

Keep one worker at two CPUs and 4 GB memory. The compound fixture seeds crop loss,
damaged infrastructure, empty fuel, a roofed refuge, SolarFlare and ToxicFallout.
The positive planning case uses a fixed Desert start and explicitly isolates
non-player starting hostiles in the disposable fixture. This does not establish
combat resolution; the Go emergency gate remains active and has separate
preemption/clearance regression coverage.
Require both worker and `run/native-go-routine-acceptance/result.json` to pass.
The scenario compares typed recovery facts with the Python census and method
reference, verifies durable damaged identities, priority promotion and bounded
roofed-refuge candidates, then checks Manual, disabled restart and cleanup.
Candidates retain exact pawn, existing area and prior restriction identities.
It requires zero routine goal methods.
This establishes planning; actual repair/refuel completion needs separate action
acceptance when those Go action families are available.

Set `RIMGOVERNOR_NATIVE_DISASTER_CAPTURE` to the exported
`run/native-go-routine-acceptance` directory and run:

```text
go -C go test -p 1 ./internal/observation -run '^TestNativeRoutineDisasterReplay$' -count=1
```

Fast policy and store tests cover condition expiry, exact-building recovery,
unknown facts, renewed episodes, Manual and world replacement. Method tests cover
player-controlled workers, no refuge, unknown restrictions, bounded stable pairs,
used methods, changed native state and cancelled goals. The native replay checks
strict decoding, method-reference parity, area candidates and reopened durable
history. Service-job candidates are covered by fast policy tests; actual service
work remains outside this acceptance scope.

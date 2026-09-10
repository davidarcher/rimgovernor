# Verify hunting screening and dispatch

[Documentation](../README.md)

Check native hunting observations and exact-target designation; actual hunting labor
needs a separate outcome assertion.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

`scripts/hunting_screen_probe.py --source-root <prepared-root> --output <new-directory>
--port 8788` starts a disposable visible colony and samples native wildlife for the
deterministic hunting screen. It retains observations, candidates and predator
rejections in `result.json`, with no hunting orders or model calls. This checks native
observation compatibility; boundary/unknown-data rejection and compiler integration are
tested separately. It does not prove reachability or successful hunting. Add
`--dispatch` to issue one designation through the deterministic compiler and shared
Hands under a scripted PLAYER goal in Manual. It verifies exact-prey native readback and
one write, then stops the disposable game. Rejection races are covered by runtime tests;
this fixture does not certify predator movement during live hunting.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)

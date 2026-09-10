# Verify native player actions

[Documentation](../README.md)

Prepare [licensed Linux inputs](docker-inputs.md) and stage current task DLLs in a
private mods directory. Run from the isolated task checkout with local Linux Docker:

```powershell
python scripts/container_player_actions.py --game <linux-game> --mods <private-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimbot-b13:my-task --output .rimbot/player-actions-01
```

Replace the placeholders with absolute paths. The output directory must not exist.
The runner builds and pins the image, starts one rendered Xvfb/llvmpipe worker with
the documented private Unity GC mitigation, and removes only its own container.
The probe script is mounted read-only and its exact SHA-256 is recorded. Use
`--no-build` only when controller code/dependencies are unchanged; the probe hash
still records the script used. Never replace shared installed DLLs.

Require exit code zero and `result.json` with `passed:true` and `cleanup_ok:true`.
`run/player-actions.json` retains native discovery schemas, requests, receipts and
independent state reads. `run/inputs.json` retains game/mod/profile hashes;
`container.log`, `run/Player.log`, display logs and failed reports remain local.
Failures are not retried automatically. Correct the cause and choose a fresh output.

The probe checks stale gizmo rejection, observed draft/undraft toggles, the Work
priority checkbox and consumed UI target rejection. It checks growing-zone crop,
expansion, cell removal and deletion, stockpile special-filter changes and invalid
filter refusal without mutation. It creates an ordinary crafting spot and uses an
available native recipe with material alternatives to verify a complete WoodLog
ingredient whitelist through the shared player-command and native execution path.

These are immediate settings and designation outcomes, not completed sowing,
hauling or production. No model inference or debug/editor operations run. Use the
[semantic command probes](semantic-commands.md) for local-model interpretation and
[resource production probes](resource-production.md) for pawn labor. The
[coverage reference](../reference/player-actions.md) defines the domain boundaries.

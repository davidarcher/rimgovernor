# Launch an inspectable native scenario

[Documentation](../../README.md) · [Local colonies](../local-colonies.md)

Use `scripts/container_scenario.py` for new script-based native runs. It stages
private inputs through `rimgovernor.container_worker`, pins the image, publishes a free
loopback dashboard port and removes only its own container when the command exits
or times out. It preserves logs, the URL in `dashboard.json` and exit/cleanup evidence
in `result.json`. Command success does not replace the scenario's native assertions.

Workers default to a two-CPU quota and 4 GiB memory ceiling with no additional
swap. `--cpus` and `--memory` configure these limits; zero/unbounded values are
rejected. Run one native worker at a time during feature validation.

The launcher reuses the content-addressed native input cache and keeps writable
state on a private Linux volume. It exports evidence only after stopping the worker
and checks every exported `.sqlite`/`.db` database. Successful runs release their
volume; failed runs retain it, and failed exports retain the container as well.
Host-side runtime files appear after export; use the dashboard during execution.
`--worker-storage bind` and `--no-input-cache` provide explicit comparison modes.

From the checkout root, with [prepared Linux inputs](docker-inputs.md):

```powershell
python scripts/container_scenario.py --game <linux-game> --mods <private-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --output .rimgovernor/watch-01 --image rimgovernor-worker:watch-01 --name "Observer acceptance" -- python scripts/scenario_dashboard_acceptance.py --seconds 30
```

The GABS input is a directory containing `gabs`. Output must be fresh. Open the
printed URL or use the [colony directory](http://127.0.0.1:8790/colonies). The link
appears while inputs stage; the observer starts when the script constructs its
first runtime. The default timeout is one hour. `--no-build` requires a worker image
with the dashboard capability label; rebuild older images.

For a sustained deterministic colony, replace the command after `--` with:

```text
python scripts/deterministic_foothold.py --source-root /worker/run --output /worker/campaign --seconds 1800 --speed Superfast
```

Each script retains its own success criteria and cleanup. Specify `--display xvfb`
on the launcher and the script's rendered option when it supports rendering. The
observer shows already-retained frames; it does not turn on capture or alter timing.

## Shared contract

The worker enables `RIMGOVERNOR_SCENARIO_DASHBOARD=1` for custom scenario commands.
`BridgeRuntime` registers with a single observer on the scenario's event loop,
including manually driven runtimes. Registration never calls `start`, launches a
game, reads native state or owns the runtime lifecycle. A replacement runtime becomes
the observed session; stopped runtimes return unavailable. Frame requests name the
session and reject stale identities. A binding failure fails startup visibly.

Only retained `/api/state` and session-scoped `/api/camera` reads are available.
HTTP mutations are refused server-side, and the UI has no controls. Native reads,
capture demand, inference and database inspection are not triggered by the observer.
The paused native acceptance above verifies unchanged game tick, direction and mode
after repeated HTTP reads and refused writes. It does not certify rendering overhead
or arbitrary gameplay under load.

The existing specialized population, player-action, husbandry and visual launchers
call the shared `dashboard_options` and `require_dashboard_image` helpers. New
launchers should use the generic launcher rather than duplicate Docker setup.
The Compose lifecycle runner already publishes its normal controller dashboard.
Fixture-only Docker tests do not launch games or need dashboard ports.

Existing running containers keep their old code and port bindings. Re-run them
with a rebuilt image to enable viewing; do not interrupt another task's native run.

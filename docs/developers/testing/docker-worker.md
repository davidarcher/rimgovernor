# Run a manual Docker worker

[Documentation](../../README.md)

Start an interactive native Linux worker with its own output, port and Compose project.
Prepare [native inputs](docker-inputs.md) first.

`docker compose up` starts the worker; it does not run scenario assertions. Use [native
Docker acceptance](docker-native.md) for the automated two-game checks.

Run commands from the task worktree root. Use private, stable input snapshots and a
fresh output directory; preserve failed results.

For a native worker, set absolute input paths and create a fresh output directory:

```powershell
$env:RIMGOVERNOR_LINUX_GAME = 'D:/RimGovernorInputs/linux-game'
$env:RIMGOVERNOR_WORKER_MODS = 'D:/RimGovernorInputs/mods-build-a'
$env:RIMGOVERNOR_WORKER_PROFILE = 'D:/RimGovernorInputs/profile'
$env:RIMGOVERNOR_LINUX_GABS = 'D:/RimGovernorInputs/linux-gabs'
$env:RIMGOVERNOR_WORKER_OUTPUT = 'D:/RimGovernorRuns/a'
$env:RIMGOVERNOR_WORKER_PORT = '8788'
New-Item -ItemType Directory $env:RIMGOVERNOR_WORKER_OUTPUT
docker compose -f containers/compose.yaml -p rimgovernor-a up --build -d
```

In a second terminal/worktree set the same input variables, select that task's mod
snapshot, and use a fresh output directory, port `8789` and project `rimgovernor-b`. Do not
use `--scale`: each worker needs its own output mount and host port. Images are built
per Compose project, so worktree changes do not replace a peer's image. The dashboard is
at `http://127.0.0.1:8788` (or the selected port).

LM Studio must accept connections from Docker on port 1234 with the configured local
model loaded. Compose explicitly permits `host.docker.internal`; it does not enable
arbitrary remote model URLs. Host firewall/server configuration may be needed. Verify a
real local model response before measuring inference. Docker's [host networking
documentation](https://docs.docker.com/compose/how-tos/networking/) and [loopback port
publishing](https://docs.docker.com/engine/network/port-publishing/) describe these
mappings.

Each startup copies game/mod binaries into the private container-local
`/opt/rimgovernor-game` directory and the prepared profile to `/worker/run`. The game uses
Linux filesystem semantics; later input DLL replacements cannot change its running
snapshot. `run/inputs.json` records staged hashes; profiles, GABS configuration/claims,
logs, controller SQLite and checkpoints stay under the output mount. The private game
copy is removed with the container. An existing `run` or private game directory is
refused, including after an incomplete startup. Preserve it as evidence and select a
fresh output for another run. `docker compose ... down` stops only that project; it does
not delete bind-mounted artifacts. Do not use Docker restart as a checkpoint restore
procedure.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)

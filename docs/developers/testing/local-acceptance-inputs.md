# Local acceptance inputs

[Testing](README.md) · [Choose checks](choose-tests.md)

## Find the shared store

This machine has complete Windows and Linux inputs at
`C:/Users/darch/code/rimgovernor/.rimgovernor/acceptance-inputs`.
They are actual copies in the RimGovernor repository, not links into another
checkout or the installed Steam game. `.rimgovernor/` is already Git-ignored;
the Docker build context excludes it too. These licensed inputs stay local.

From any worktree of this repository, resolve the store through Git's common
directory (do not look only in the task worktree's `.rimgovernor/`):

```powershell
$repoRoot = Split-Path (git rev-parse --path-format=absolute --git-common-dir) -Parent
$inputs = Join-Path $repoRoot '.rimgovernor/acceptance-inputs'
Get-Content "$inputs/inventory.json"
```

On a Linux checkout, the equivalent discovery is:

```sh
repo_root="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")"
inputs="$repo_root/.rimgovernor/acceptance-inputs"
```

A separate clone does not inherit ignored files. Transfer the licensed input
store to that clone locally if needed. On this Windows host, Docker receives the
Windows absolute paths as bind mounts; never use Windows game binaries in Linux.

| Location under the store | Contents |
| --- | --- |
| `windows/game` | Windows RimWorld, Data/DLC, Unity and managed build references; Mods are staged separately. |
| `linux/game` | Linux RimWorld, Data/DLC, Unity and managed build references; Mods are staged separately. |
| `<platform>/mods` | Harmony, RimBridgeServer and a complete production RimGovernor package. |
| `<platform>/gabs` | Platform-specific GABS executable, license and release documentation. |
| `<platform>/profile` | Prefs, current native package selection and `Saves/RimGovernor-tribal8-baseline.rws`. |
| `inventory.json`, `files.sha256.json` | Local input validation, file sizes and SHA-256 hashes. |
| `origins.json`, `steam-linux-depots.json`, `gabs-release.json` | Copy provenance and retained upstream release/depot records. |

The store is a stable source, not a running colony. Copy writable inputs to fresh
`.rimgovernor/` paths in your task worktree. Do not launch the shared profile,
modify shared DLLs, or reuse another task's output, image tag or GABS config.
The supplied production package is a snapshot: build your task's native code and
required fixture flags into its private Mods before accepting changed behavior.
The baseline is eight-tribal; scenario-specific setup still belongs to the probe.

## Windows private run

Run from your task worktree after setup. This copies the game, dependencies and
GABS, builds the task package, and creates a private profile/config without
pointing at Steam or another task's game. Choose a fresh `$trial` each time.

```powershell
$win = Join-Path $inputs 'windows'
$trial = Join-Path $PWD '.rimgovernor/acceptance-win-01'
if (Test-Path $trial) { throw 'Choose a fresh trial directory' }
New-Item -ItemType Directory "$trial/game/Mods", "$trial/gabs" -Force | Out-Null
Copy-Item "$win/game/*" "$trial/game" -Recurse
foreach ($mod in @('Harmony', 'RimBridgeServer')) {
    Copy-Item "$win/mods/$mod" "$trial/game/Mods" -Recurse
}
Copy-Item "$win/gabs" "$trial/gabs/gabs-v1.1.1-windows-amd64" -Recurse
./scripts/build_native_mod.ps1 -RimWorldManagedDir "$trial/game/RimWorldWin64_Data/Managed" -HarmonyAssembly "$trial/game/Mods/Harmony/Current/Assemblies/0Harmony.dll" -RimBridgeSdkDir "$trial/game/Mods/RimBridgeServer/1.6/Assemblies" -OutputRoot "$trial/native-build"
Copy-Item "$trial/native-build/RimGovernor" "$trial/game/Mods" -Recurse
$env:PYTHONPATH = Join-Path $PWD 'controller'
.venv/Scripts/python.exe scripts/prepare_bridge_trial.py --source-profile "$win/profile" --rimworld "$trial/game" --root "$trial" --observations
```

Use `-DotNet 'C:/Program Files/dotnet/dotnet.exe'` if .NET is absent from PATH.
Select a [focused probe](headless-probes.md) that accepts `--source-root` or
`--prepared-root`, and inspect its `--help` before passing `$trial`. Some older
probes hardcode `.rimgovernor/bridge`; do not assume they accept arbitrary roots.
Never substitute the shared input store for a writable probe root.

## Linux / Docker private run

From the task worktree, use the shared Linux game/GABS/profile with private task
mods. The container launcher copies its read-only inputs into a private worker.
The image includes task controller/dashboard source, not the licensed inputs.

```powershell
$linux = Join-Path $inputs 'linux'
$taskMods = Join-Path $PWD '.rimgovernor/acceptance-linux-mods-01'
if (Test-Path $taskMods) { throw 'Choose fresh task mods' }
New-Item -ItemType Directory $taskMods | Out-Null
foreach ($mod in @('Harmony', 'RimBridgeServer')) {
    Copy-Item "$linux/mods/$mod" $taskMods -Recurse
}
./scripts/build_native_mod.ps1 -RimWorldManagedDir "$linux/game/RimWorldLinux_Data/Managed" -HarmonyAssembly "$taskMods/Harmony/Current/Assemblies/0Harmony.dll" -RimBridgeSdkDir "$taskMods/RimBridgeServer/1.6/Assemblies" -OutputRoot .rimgovernor/acceptance-linux-build-01
Copy-Item .rimgovernor/acceptance-linux-build-01/RimGovernor $taskMods -Recurse
python scripts/container_native_acceptance.py --game "$linux/game" --mods "$taskMods" --profile "$linux/profile" --gabs "$linux/gabs" --image rimgovernor-worker:my-task --output .rimgovernor/acceptance-linux-01
```

For script-based gameplay checks use
[scripts/container_scenario.py](scenario-launcher.md), the same four input
arguments, and the selected scenario command after `--`. Add the scenario's
`-Fixture` flags to the native build and `--display xvfb` when rendering matters.
Use a fresh output and task-specific image tag; rebuild for changed source.

## Diagnose an unavailable sandbox

Check the store and inventory before reporting missing licensed inputs. Confirm
Docker Desktop is running Linux containers, or that the Windows tools can start
an owned private process. Missing Python/.NET/Docker on PATH, restricted process
permissions, package restore/network failure, a stale task DLL, and missing
scenario fixtures are distinct failures: report the exact failed command and
artifact. Existing files do not override the current task's filesystem/process
permissions; use the normal approval path for an actual restriction.

Input inventory and successful compilation establish available inputs, not a
passing gameplay scenario. Retain native logs and scenario assertions for actual
acceptance; see [Docker inputs](docker-inputs.md) for refresh/provenance rules.

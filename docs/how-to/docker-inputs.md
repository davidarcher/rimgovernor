# Prepare native Docker inputs

[Documentation](../README.md)

Stage licensed Linux inputs before starting a native worker. Keep them stable throughout
staging.

Run commands from the task worktree root. Use private, stable input snapshots and a
fresh output directory; preserve failed results.

Docker Engine/Desktop with Linux containers and Compose is required. The image build
runs dashboard typechecking/tests/build; the `tests` target runs the Python suite.
Windows-specific tests skip on Linux. Native acceptance is separate. Compose workers set
`gc-max-time-slice=0` in their private Unity boot.config to mitigate observed Mono
startup crashes. Original game inputs remain unchanged. Set
`RIMBOT_UNITY_GC_TIME_SLICE=source` to preserve the original setting for comparisons.
The source boot hash, prepared boot hash and effective override are retained in
`staging.json`/`inputs.json`. The [named runner](native-scenarios.md) supports repeated
original/overridden GC trials, exact paused startup, three-day endurance and sampled
resource comparisons. These are bounded observations, not a native GC-pause profiler.
Windows game executables cannot run in this image. Supply your licensed Linux RimWorld
installation (including Data/Mono files), a Linux amd64 GABS executable named `gabs`, a
complete `Mods` directory, and a prepared profile containing `Config/Prefs.xml`,
`Config/ModsConfig.xml` and `Saves/RimBot-tribal8-baseline.rws`. The save is copied
unchanged.

The mod directory must contain Core/DLC content where required by the game, Harmony,
RimBridgeServer, RimBotObservations (including its identity assembly and BridgeTools)
and RimBotHeadless. Resolve workshop links into this snapshot and match the active
package IDs in ModsConfig.xml. Build task DLLs into a private staging directory using
the native projects' path properties; do not use `-Install` on a shared running Windows
installation. Finish staging all inputs before launch. Images contain
controller/dashboard code only; licensed game files are mounted at runtime and never
included in the build context.

## Obtain Linux files through Steam

Use the signed-in Steam client's console (`steam://nav/console`) to query
`app_info_print 294100`. In its `depots` section, choose the base depot with `oslist`
set to `linux` (294103) and its current `public` manifest. Run `download_depot 294100
294103 <manifest>`. Steam reports completion and the separate
`steamapps/content/app_294100/depot_294103` directory; this does not switch the
installed Windows game's platform. Wait for completion before copying the files.

For installed DLC, select each Linux depot with the corresponding `dlcappid` from the
same metadata and download it through the signed-in client. Royalty, Ideology, Biotech
and Odyssey use 1149643, 294108, 367686 and 294116 respectively. Use Steam's current
manifests, and download only owned DLC required by the profile. Copy the base depot into
a fresh private game directory, then merge the downloaded DLC `Data` directories into
that directory's `Data`. Retain depot/manifest IDs and `Version.txt` beside the test
evidence. Never put licensed inputs in Git or images.

Copy Harmony and the complete RimBridgeServer mod into the private mod directory. Build
task-local companion binaries against the Linux references, then copy their
About/Assemblies/BridgeTools folders into private RimBotObservations and RimBotHeadless
directories. For example (use an available .NET SDK):

```powershell
dotnet build integrations/colony-bridge/src/ColonyObservations.csproj -c Release "-p:RimWorldManagedDir=<linux-game>/RimWorldLinux_Data/Managed" "-p:RimBridgeSdkDir=<private-mods>/RimBridgeServer/1.6/Assemblies" "-p:HarmonyAssembly=<private-mods>/Harmony/Current/Assemblies/0Harmony.dll"
dotnet build integrations/headless-rim/src/HeadlessRim.csproj -c Release "-p:RimWorldManagedDir=<linux-game>/RimWorldLinux_Data/Managed" "-p:HarmonyAssembly=<private-mods>/Harmony/Current/Assemblies/0Harmony.dll"
```

Use a Linux GABS release matching the tested bridge version, verify the upstream release
asset SHA-256, and retain its LICENSE/provenance alongside the executable. Do not
install these task builds into the shared Windows game.

## Reuse Docker input snapshots

The native acceptance runner automatically caches the game, mods and GABS in a local
Docker volume named `rimbot-inputs-v1-<sha256>`. It hashes all source file contents on
the host, excluding the game's `Mods` directory in favor of the explicit mod input.
Changed files, additions and removals select a different volume even when sizes and
timestamps are unchanged. Symlinks are dereferenced; directory cycles are refused.
Keep source inputs stable while hashing and uploading.

On a miss, the helper uploads a plain-file archive and verifies every file before
atomically publishing the snapshot. Concurrent publishers serialize with a Linux lock.
Hits verify cached contents inside Docker and do not recopy inputs across Windows bind
mounts. Corrupt snapshots fail visibly; incomplete uploads are retained and never used.
Workers mount the snapshot read-only, then make private container-local copies. Saves,
preferences, mod selection, GABS claims and the Unity GC override remain per-worker.
Licensed files stay in local volumes, outside images and Git.

Use `--no-input-cache` with `container_native_acceptance.py` for a direct-bind comparison.
The runner retains `cache.json` (key, hit/miss, host hash time and total preparation
time), `cache-manifest.json` and helper logs. Each worker's `run/staging.json` records
its cache key and private-copy duration. Compare cache preparation plus worker startup
when measuring end-to-end savings; a first upload is additional setup work.

For manual Compose runs, prepare a snapshot with the same built worker image:

```powershell
python scripts/container_input_cache.py --game <linux-game> --mods <private-mods> --gabs <linux-gabs-directory> --image rimbot-worker:my-task --output .rimbot/cache-01
docker compose -f containers/compose.yaml -f .rimbot/cache-01/cache-compose.json up --no-build
```

Set the normal Compose input/output environment variables first. The profile still
comes from its bind mount; game/mod/GABS binds are superseded by the cached input paths.
Rerun preparation after editing any input; do not reuse an old generated override for
new source files. Cache volumes survive `compose down`. When no worker uses an obsolete
snapshot, remove only its exact volume name from `cache.json` with `docker volume rm
<volume-name>`. No automatic pruning or cache eviction runs.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)

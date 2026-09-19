# Encrypted remote acceptance dependencies

[Developer guide](README.md) · [Remote contract](contracts/remote-acceptance.md)

`go run ./cmd/remotebundle` (from `go/`) packages dependencies and bootstraps a
Windows runner. Game bytes are age-encrypted release assets in the public source
repository. The shared cache contains those same encrypted parts. The identity
needed to decrypt them is a trusted-run secret; a private repository or separate
download credential is not required.

## Package and publish

Install pinned [age](https://age-encryption.org/) and
[7-Zip](https://www.7-zip.org/download.html) executables. The pack command accepts
their absolute paths. `age-keygen -o identity.txt` creates a maintainer-held
identity; `age-keygen -y identity.txt` prints its public recipient. Store the
identity outside the checkout with restricted access. Never commit or upload it.

Create an empty public release through the maintainer's normal release process
and record its numeric ID. Packaging itself does not contact or publish to GitHub.
Use exact installed dependency versions, not `latest`:

```powershell
go run ./cmd/remotebundle pack -repo C:\rg\src `
  -game C:\inputs\RimWorld -game-version '1.6.4871 rev590' `
  -harmony C:\inputs\Harmony\Current\Assemblies\0Harmony.dll `
  -harmony-mod C:\inputs\Harmony -harmony-version <assembly-version> `
  -bridge C:\inputs\RimBridgeServer -bridge-version <version> `
  -gabs C:\inputs\gabs.exe -gabs-version <version> `
  -origin davidarcher/rimgovernor -release-id <numeric-id> `
  -recipient <age-public-recipient> -key-id <rotation-label> `
  -7z C:\tools\7zr.exe -age C:\tools\age.exe -out C:\rg\bundle
```

The destination must be new. `CorePaths` in `internal/remotebundle/pack.go` is
the game inclusion list: executable, Unity/Mono runtime, Core data, version and
license notices. All Core/Unity media is retained; the media removal allowlist
is empty. Rendered cases therefore retain their textures/shaders but still need
a suitable display/graphics environment; headless extraction does not prove
rendered-case support. Expansions, other mods, profiles and player saves are not
selected. Harmony, the bridge runtime/SDK and GABS are separate inventoried
components. Source junctions are materialized into regular files. Packaging
never writes to the source install or copies branch-built RimGovernor binaries.

`inventory.json` records every plaintext file and its digest. `bundle.draft.json`
records exact dependency versions, component inventory digests, encryption key
ID and ordered encrypted parts. Parts default to 1,000,000,000 bytes (maximum
1,900,000,000) and have digest-bearing immutable names. The command reports both
unpacked and encrypted compressed sizes; 1 GB is a target, not a reason to remove
untested files. Stable inputs produce the same inventory; age intentionally
randomizes ciphertext, so each encryption produces a new bundle identity.

Publication is an **explicit maintainer operation**, using authenticated `gh`:

```powershell
go run ./cmd/remotebundle publish -manifest C:\rg\bundle\bundle.draft.json
```

The publisher verifies that the pinned origin is public, uploads encrypted parts
and a digest-named inventory without overwriting existing assets, reads back
asset IDs, then uploads `bundle-<sha256>.json`. Do not use the draft manifest for
bootstrap. Keep the printed final manifest and inventory with the trusted run
inputs and pin the exact manifest SHA-256. A partial publication is not usable;
use a fresh release/output for a new attempt. No plaintext directory belongs in
GitHub releases, Actions caches or ordinary workflow artifacts.

To refresh, produce and verify a new bundle and change the trusted manifest pin.
To roll back, select the previous manifest digest and matching age identity.
Never replace old assets in place. A corrupt cache hit fails and must be removed
before retrying; there are no broad cache restore prefixes. Key rotation creates
a new recipient/key ID and fresh encrypted assets; previously public ciphertext
cannot be revoked if its old identity is compromised.

## Fixtures and generated starts

Committed saves are inventoried from `scripts/fixtures/saves` and used from the
tested checkout. They are not copied out of a player's profile. By default the
bundle includes `starts/compatibility.json` with an empty `generated` list;
bootstrap reports that generated debug starts will regenerate on first use.

`pack -starts <directory>` accepts explicitly prepared debug starts accompanied
by `compatibility.json`. Preserve that metadata from the dependency/source
revision that produced the starts; do not relabel old saves with current hashes.
It contains schema version 1, exact `game_version`, `native_source_sha256`,
`dependencies_sha256`, and `committed_fixtures`/`generated` arrays of
`{path, bytes, sha256}`. The dependency digest hashes sorted inventory lines
`path\tbytes\tsha256\n` for all non-start components. The native digest is the
existing native source-tree hash. Only named `RimGovernor-debug-*.rws` files are
copied; their hashes and Core-only mod metadata must validate. Unrelated files
in the input directory are never copied.

Packaging rejects incompatible supplied starts. Bootstrap verifies the bundled
dependency identity and generated save hashes; native-source or committed-save
changes visibly skip staging and let the harness regenerate. Compatible saves
are copied to the job's ordinary and headless profiles. Mutable generated saves
never return to the shared encrypted dependency cache.

## Trusted Windows bootstrap

The workflow owner supplies `scripts/bootstrap_remote.ps1` and its authorization
and tool lock from the **trusted workflow revision**, before running selected
source. Authorization is a JSON object with `repository`, `tested_commit`,
`workflow_commit` (full 40-hex revisions) and `event` (`workflow_dispatch`, `schedule` or
`push`). Manual dispatch means the maintainer reviewed the exact tested commit;
push and schedule require protected `refs/heads/main`. The script and Go bootstrap verify
these against the checkout and GitHub environment before cache access. This
check is a guard, not a substitute for trusted workflow placement or review.

The workflow must apply the same gate **before** its Actions cache restore and
before exposing the age identity. Restore only the exact key printed in the
report: `rimgovernor-bundle-v1-windows-x64-<manifest-sha256>`. The entry script
handles a cold miss by downloading pinned release asset IDs; both cold and warm
paths authenticate origin metadata and verify sizes/digests before decryption.
The cache path is the key's directory under `-Cache`, never the whole job root.

The checked-in [Windows tool lock](../../scripts/remote-tools.windows.json)
pins verified release bytes. Its schema version is 1 with exactly four `tools` entries:
`go`, `dotnet`, `age`, `7z`. Each entry requires `name`, exact `version`, HTTPS
`url`, lowercase archive `sha256`, `format` (`zip` or single-file `exe`) and
relative `executable` path. Use official release downloads and reviewed hashes.
Go must match `go/.go-version`; .NET SDK must match the lock. Examples of
executable paths are `go/bin/go.exe`, `dotnet.exe`, `age/age.exe`, `7zr.exe`.
The script installs these portable tools privately, with no system-wide changes
or unpinned package-manager upgrades. PowerShell 7, Git and `gh` are prerequisites
of the trusted Windows runner image. The workflow pins its image/actions.
The official 7zr download URL is mutable; its pinned digest deliberately refuses
a newer download until a maintainer reviews and refreshes the lock.

```powershell
./scripts/bootstrap_remote.ps1 -Repo C:\rg\src -Work C:\rg\j1 `
  -Cache C:\rg\ciphertext -Manifest C:\rg\inputs\bundle-<digest>.json `
  -ManifestSHA256 <digest> -Trust C:\rg\inputs\trust.json `
  -ToolLock C:\rg\inputs\tools.json -Identity C:\rg\secrets\identity.txt `
  -Role fixture -Suite C:\rg\inputs\suite-s1.json
```

Use a fresh short work directory for each job. Bootstrap records available disk,
OS/architecture, CPU count, image version, tool-lock digest, cache hit and
provision/restore/extract/build/total timings in `job/bootstrap.json`. It checks
disk against declared archive/extracted sizes before extraction and runs
`acceptance doctor` after setup. All dependencies are explicit through
`acceptance setup -explicit -layout ... -rimworld ... -bridge ... -sdk ...
-harmony ... -harmony-mod ... -gabs ...`; Steam, sibling worktrees and player preferences are
unused. The game copy's internal junctions target that job's verified extracted
dependencies, never the developer machine.

Fixture and production roles use separate work directories and must both finish
setup before their games start. Run each shard with one worker. GABS and native
acceptance retain their existing root-specific port allocation. Setup refuses
to replace game/GABS files or rebuild binaries while an owned game/harness is
running. The optional suite uses the planner's projection, a fresh output and a
45-minute timeout. Its observed native postconditions, not bootstrap success,
are the gameplay verdict.

The script cleans its own game/GABS/controller/acceptance PIDs in `finally`.
The workflow must also call `cleanup_remote.ps1 -Work <job-work>` in an
`always()` cancellation step, then discard the job's plaintext directory and
identity. Cleanup matches executable directory boundaries and never sweeps
peers by image name. Upload only the downstream sanitized evidence allowlist;
neither bootstrap's dependencies/tools nor profiles are public artifacts.

## Verification

`go run ./cmd/test` runs manifest, inventory, path, cache, trust and setup tests.
Set `RIMGOVERNOR_TEST_AGE` and `RIMGOVERNOR_TEST_7Z` to absolute executables to
include the synthetic encrypted multipart round trip. The Windows junction
test proves source materialization without copying links.

`remotebundle verify -manifest <published-json> -sha256 <digest> -parts <dir>`
checks the pinned manifest, inventory and ciphertext. `extract` adds `-out
<new-short-path> -identity <file> -age <exe> -7z <exe>` and checks archive names,
links, sizes and the exhaustive extracted inventory. These commands do not
prove gameplay or hosted runner operation. Cold/warm native smoke and rendered
case validation require the real published bundle and trusted workflow.
Use `-local-draft` with offline `verify` or `extract` to check a package before
publication. Bootstrap never accepts draft manifests or missing asset IDs.

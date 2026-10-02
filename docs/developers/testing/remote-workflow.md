# Remote Windows workflow

[Evidence and import](remote-evidence.md) · [Bundle setup](../remote-bundles.md) ·
[Contract](../contracts/remote-acceptance.md)

`.github/workflows/remote-acceptance.yml` runs nightly or by manual dispatch;
pushes do not trigger CI. Keep the workflow branch selector on protected `main`.
Manual dispatch accepts `tested_ref` (any same-repository branch or tag, default `main`)
or an overriding `tested_commit` SHA, tier and shard count.
The gate resolves the source once to a full SHA shared by native, race and protobuf jobs.
`base_commit` is required for land; smoke/full default it to the resolved source SHA. The nightly
07:23 UTC schedule selects full against its immutable main commit. Both scheduled
and manually dispatched full tiers also run the Go race, protobuf generation drift
and protobuf proof checks.
The `cases` tier runs only the `cases` input: comma-separated registry names or bare
areas (`power` selects every `power/*` case). A name matching nothing refuses the plan. Agents use
it to offload targeted runs, for example
`gh workflow run remote-acceptance.yml --ref main -f tier=cases -f cases=power,food/reserve -f shards=2`.
Add `-f label=<issue or agent>` to name a run: the label and the cases appear in the run title
(`gh run list`), so concurrent runs can be told apart.
To tell an intermittent case from a regression, add `-f repeat=N` (cases tier, 1-50): every
shard runs N times and the verdict summary lists `case: passed/N passed`. Run 1 alone is the
verdict and landing evidence; repetitions 2..N upload `soak-*` artifacts that feed only the rate.
After the verdict, `remoteaccept index -root C:\rg\evidence` (display only, continue-on-error)
writes `index.json` into the verdict artifact and appends a table to the run summary, failing
cases first: each case's status, attempt count, shard, the `gh run download -n` artifact name
(`acceptance-<run>-<attempt>-shard-<id>`), up to eight postmortem lines from its report's
`diagnosis`, and its paths inside that artifact: `<shard>/fixture/<case>/result.json` (native
report), `<shard>/fixture/<case>/flight.jsonl`, `<shard>/fixture/<case>.log` and
`<shard>/fixture/snapshots/<case>/routine-stream-*.jsonl`. A path is listed only when exported.
Reruns need
**Re-run all jobs**: an attempt cannot borrow a previous attempt's plan/artifacts.

Activation remains closed until a maintainer publishes the workflow and encrypted
bundle, reviews billing/storage controls and configures the following repository
variables (not environment variables, so job-level settings are consistent):

| Variable | Value |
| --- | --- |
| `REMOTE_ACCEPTANCE_ENABLED` | `reviewed-v1`, only after capacity/storage and stop-usage controls have been reviewed |
| `REMOTE_ACCEPTANCE_MAINTAINERS` | Comma-separated GitHub logins authorized to review tested commits and dispatch/rerun |
| `REMOTE_BUNDLE_ASSET` | Numeric published bundle manifest asset ID |
| `REMOTE_INVENTORY_ASSET` | Numeric inventory asset ID |
| `REMOTE_BUNDLE_SHA256` | SHA-256 of exact published manifest bytes |

Create the `remote-acceptance` environment, restrict it to protected `main`, and
store the age identity as its `REMOTE_BUNDLE_IDENTITY` secret. Use required
reviewers according to the maintainer's trust policy. Main branch protection and
review must establish trust of pushed code. Dispatch review covers the **exact
tested commit**, including build scripts. The workflow also checks both original
and rerun actor allowlists. A fork, task-ref workflow, PR event, unprotected ref,
missing activation or changed repository visibility fails before cache access or
decryption. Public source is the configured v1 policy; changing visibility requires
revalidation, not automatic use of paid Windows minutes.

Each job uses `windows-2022`, one native worker, up to 32 nonempty shards and at
most 20 active shard jobs per run. The default smoke dispatch uses two shards.
Manual, scheduled and push runs overlap; no concurrency group serializes or
cancels them. GitHub enforces the account-wide runner capacity.
Planner/aggregation jobs have ten-minute limits; shard jobs have
360 minutes including a shared 345-minute allowance across both role suites.
Known case budgets must fit before workers start. The production retry classifier
remains empty, so runs use `max_attempts: 1` without reserving an unused retry.

Fixture and production layouts are bootstrapped separately before either starts.
Only encrypted parts use the exact digest cache key, without restore prefixes.
The cache and age identity remain outside the tested checkout. The identity is
removed after bootstrap, and the suite inherits no download token. An `always()`
cleanup also stops only this job's processes and removes any remaining identity;
hosted runner disposal removes the private game layouts. A hard platform kill
can prevent cleanup/upload, which aggregation reports as missing evidence.

## Verdicts and diagnostics

Each shard owns a live check run on the workflow run page. Its name shows the
completed count and running case; its table shows queued, running and terminal
case states. A trusted observer polls the planned registry paths every five
seconds and updates GitHub only on transitions. It receives the token on stdin
before the suite environment is scrubbed; native reports contribute only the
passed boolean and numeric wall time. The check summary identifies the tested
commit and links uploaded shard diagnostics. Missing results fail the display;
verdict cleanup cancels abandoned checks for the same run attempt. API failures
disable progress without affecting execution or the authoritative aggregate.

Plan, shard and final verdict artifact names include the Actions run ID and
attempt, and shard artifacts also include the shard ID. Retention is seven days.
Planner refusals are retained in `planner.log`; without a selection, collection
records the refusal in `incomplete.json` and the job summary, and fails before
shard aggregation. Job summaries link the diagnostic and final artifacts. The final `verdict` artifact
is the one to [import](remote-evidence.md#import-an-authenticated-actions-artifact).
Pin workflow path `.github/workflows/remote-acceptance.yml` and its reviewed
published revision when configuring the importer.

The trusted exporter copies JSON/JSONL/log/text/Markdown and generated PNG frames
from suite output only,
excludes worker/profile/checkpoint trees and game binary/save files, rejects links,
unsafe paths, duplicate JSON keys and recognized private-key/save/binary content,
redacts supplied token values and private runner paths, and rehashes references
after normalization. PNG frames are decoded, limited to 4096 pixels per dimension
and re-encoded to strip metadata/trailing payloads; raw game textures remain
prohibited. This is a boundary for **reviewed diagnostic producers**,
not a way to make hostile tested code safe. Never add a diagnostic containing
licensed file contents or secrets. A role bootstrap report is projected to public
runner measurements; dependencies, tools and profiles are never uploaded.

Public runs have no local raw-diagnostic byte cap by default. The optional
repository variable `REMOTE_ARTIFACT_MAX_BYTES` sets a positive run cap; zero
disables it. A configured cap reserves 64 MiB for manifests and divides the
remainder across shard and final copies. This is an operator limit, not a GitHub
plan quota: GitHub stores compressed artifacts and enforces its own service
limits. See [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions).
Required reports/logs are exported first. A size limit, malformed record,
missing required file or unsafe diagnostic stops export; partial diagnostics and
an `incomplete.json` marker remain available. Nothing is silently truncated into
a green report. Aggregation authenticates job conclusions, covers every planned
shard, and recomputes native case verdicts. Cancellation, missing output or a
failure outside the native suite cannot yield a passing aggregate.

## Fixture factory

`.github/workflows/fixture-factory.yml` (weekly, or dispatched) runs `sustained/colony` through the same gate, plan and
bootstrap phases (`FACTORY_CASES` pins the case list) and uploads its
checkpoint ring, saves and stores included, plus `factory.json` naming the
tested commit, as the artifact `colony-checkpoints-<commit>`. Unlike shard
diagnostics this deliberately publishes generated saves; they carry def
references, never licensed file contents. Retention is 90 days, so the newest
bundle outlives the weekly cadence. A fresh clone consumes it with
`acceptance profile-capture -root <dir> -from latest-ci`, or `acceptance
fetch-fixture -root <dir>` to print the newest downloaded bundle directory for snapshot
tests; both `gh run download` the newest successful run once into
`<root>/ci-fixtures/<run id>`.

Every remote run records [colony snapshot streams](colony-snapshots.md):
`remote_workflow.ps1` sets `RIMGOVERNOR_SNAPSHOT_DIR=<job output>\snapshots`,
so each shard's public diagnostics carry
`<shard>/<role>/snapshots/<area>/<case>/routine-stream-*.jsonl` (under a
`REMOTE_ARTIFACT_MAX_BYTES` cap a stream that no longer fits is left out
rather than failing the shard), and the factory artifact carries
`snapshots/sustained/colony/` beside its ring. `fetch-fixture` names the
downloaded streams on stderr; a snapshot test loads one review with
`snapshot.LoadReview(<stream>, <tick>, 0)`, or trims it into `testdata/` as
[Recording](colony-snapshots.md#recording) describes.

`.github/workflows/snapshot-perf.yml` runs nightly: it fetches that bundle,
bootstraps a fixture-role layout (`remote_workflow.ps1 -Phase profile`) and
runs `profile-capture -n 50 -json`; a Linux job then runs `acceptance
profile-compare`, which uploads `snapshot-perf-<commit>` (`record.json`, 30
days) and compares each family's and the total's p90 with the median p90 of
the last seven successful runs' records. A span more than 30% and 0.5 ms over
its baseline (at least three samples) opens, or comments on, the issue
"Snapshot capture regression (nightly snapshot-perf)" with the commit range.
Tune `-threshold`/`-min-ms` in the workflow for runner noise.

## Validation and activation evidence

`go run ./cmd/test` covers authorization refusals, portable multi-shard export,
an injected red native assertion, missing/corrupt diagnostics, content rejection,
limits, aggregation/import and schedule provenance. These are synthetic tooling
checks; no game is launched. Validate workflow syntax with actionlint v1.7.12.

The remote full tier excludes rendered cases because hosted Windows has no
usable GPU. The plan and aggregate record these as `skipped` with reason
`rendered`; they remain in the local full tier. There is no automatic paid GPU
fallback. Known runnable case budgets must still fit the bounded shards.

Before claiming hosted operation, retain cold and warm smoke runs, a native
failure with accessible diagnostics, cancelled/missing-shard evidence, and a
verified import. [#382](https://github.com/davidarcher/rimgovernor/issues/382)
records the completed smoke/failure/import proofs; hosted cancellation remains
distinct from synthetic cancellation coverage. [#383](https://github.com/davidarcher/rimgovernor/issues/383)
owns land-tier rollout measurements. Follow the [maintainer-to-agent handoff](remote-handoff.md)
for publication, dispatch, retrieval and cancellation. Agents need separate
maintainer authorization to publish source/bundles or dispatch runs.

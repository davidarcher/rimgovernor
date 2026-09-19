# Remote Windows workflow

[Evidence and import](remote-evidence.md) · [Bundle setup](../remote-bundles.md) ·
[Contract](../contracts/remote-acceptance.md)

`.github/workflows/remote-acceptance.yml` runs from protected `main`. Pushes
select smoke using the event's exact before/after commits. Manual dispatch
accepts full ancestor/base and tested commit IDs, tier, shard count and an
explicit attestation that the maintainer reviewed the tested code. The nightly
07:23 UTC schedule selects full against its immutable main commit. Reruns need
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
most four active shard jobs. The default smoke dispatch uses two shards. One
workflow runs at a time with `queue: max` and cancellation disabled; later pushes
do not replace an active landing or the pending queue. GitHub's queue capacity
still applies. Planner/aggregation jobs have ten-minute limits; shard jobs have
60 minutes including a shared 45-minute allowance across both role suites.
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

Plan, shard and final verdict artifact names include the Actions run ID and
attempt, and shard artifacts also include the shard ID. Retention is seven days.
Job summaries link the diagnostic and final artifacts. The final `verdict` artifact
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

The 1 GiB run allowance includes duplicate shard/final uploads: 64 MiB is reserved
for manifests, and the remaining space is divided among shards and their final
copies. Required reports/logs are exported first. A size limit, malformed record,
missing required file or unsafe diagnostic stops export; partial diagnostics and
an `incomplete.json` marker remain available. Nothing is silently truncated into
a green report. Aggregation authenticates job conclusions, covers every planned
shard, and recomputes native case verdicts. Cancellation, missing output or a
failure outside the native suite cannot yield a passing aggregate.

## Validation and activation evidence

`go run ./cmd/test` covers authorization refusals, portable multi-shard export,
an injected red native assertion, missing/corrupt diagnostics, content rejection,
limits, aggregation/import and schedule provenance. These are synthetic tooling
checks; no game is launched. Validate workflow syntax with actionlint v1.7.12.
That release lacks GitHub's newer `concurrency.queue` property; its exact
`unexpected key "queue" for "concurrency" section` diagnostic is the only
allowed exclusion. GitHub documents the field in its
[concurrency guide](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

The full tier includes rendered video cases. They run through the existing
windowed profile without batch/no-graphics flags and must produce real frames.
Hosted display/graphics support is unverified until those cases run; it is not a
planner exclusion. Standard Windows runners do not promise a dedicated GPU.
Unity supports a software Direct3D WARP option, but its suitability for this
RimWorld build and image needs measurement before changing launch flags. There
is no automatic paid GPU fallback. Known case budgets must still fit the bounded
shards; [#387](https://github.com/davidarcher/rimgovernor/issues/387) owns the
nightly rollout and its measured resource requirements.

Before claiming hosted operation, retain cold and warm smoke runs, a native
failure with accessible diagnostics, cancelled/missing-shard evidence, and a
verified import. [#382](https://github.com/davidarcher/rimgovernor/issues/382)
records the completed smoke/failure/import proofs; hosted cancellation remains
distinct from synthetic cancellation coverage. [#383](https://github.com/davidarcher/rimgovernor/issues/383)
owns land-tier rollout measurements. Follow the [maintainer-to-agent handoff](remote-handoff.md)
for publication, dispatch, retrieval and cancellation. Agents need separate
maintainer authorization to publish source/bundles or dispatch runs.

# Remote acceptance contract v1

[Contracts](README.md) · [Choosing checks](../testing/choose-tests.md) ·
[Delivery epic #363](https://github.com/davidarcher/rimgovernor/issues/363)

This is the implementation contract for #377–#383. Bundle/bootstrap tooling is
documented [here](../remote-bundles.md); hosted workflow activation is separate. Local `acceptance list`, `acceptance suite` and `cmd/land` remain the
execution and landing interfaces. The coordinated JSON [examples](remote-acceptance/)
are synthetic fixtures, never acceptance evidence or downloadable game files.

## Ownership and compatibility

Every versioned remote document is a UTF-8 JSON object with `schema_version: 1`. All fields in
the examples are required unless marked optional below. Producers reject
duplicate keys; consumers reject missing required fields, unknown versions,
unknown enum values, malformed identities and inconsistent references. Additive
optional fields may be ignored; removing fields, changing meanings or adding
required fields needs a new version and coordinated reader support. No reader
may default an unknown verdict to success. #376 owns this shared vocabulary;
#382 coordinates compatibility across the implementing owners.

| Document / field owner | Fields and meaning |
| --- | --- |
| #377 bundle publisher, `bundle.json` | `schema_version`; `game` (exact `version`, `platform`, `core_only`); `components` (`name`, `version`, `path_prefix`, `sha256` of each packaged component's inventory); `origin` (`repository`, numeric `release_id`); `parts` (`asset_id`, `name`, integer `bytes`, `sha256`, in extraction order); `unpacked_bytes`; `inventory` reference; `encryption` (`format`, nonsecret `key_id`). Includes Core game, Harmony, bridge SDK/GABS and cached starts; no production mod/controller binaries from another revision. |
| #377 bundle publisher, `inventory.json` | `schema_version`, `files`: exhaustive, path-sorted `{path, bytes, sha256}` of extracted regular files. Component inventory digest hashes the UTF-8 LF-terminated lines `path\tbytes\tsha256\n` for its files. Components have disjoint `path_prefix` roots covering the inventory. |
| #382 dispatcher, `run.json` | `schema_version`, `run_id`, `repository`, `trigger`, `workflow_commit`, `tested_commit`, `base_commit`, `tier`, `bundle`, `limits`. `trigger` has `event`, `actor`, `published_ref`, integer `actions_run_id`, integer `actions_run_attempt`. The ref is provenance, never a checkout identity. |
| #379 planner, `selection.json` | `schema_version`, `run` reference, `planner_commit` (equals tested commit), `diff_mode`, sorted unique `changed_files`, `cases` (`name`, nonempty `reasons` array), `sampled_areas`, `algorithm`, `shards` (`id`, ordered `cases` list). Reasons are `smoke`, `affected:<area>`, `sampled:<area>` or `full` for the nightly full tier. |
| #381 executor with #378 bootstrap, `attempts.json` | `schema_version`, `run` and `selection` references, `shard_id`, `runner`, `attempts`. `runner` records `os`, `image_version`, `arch`, `cpu_count`, `memory_bytes`, `free_disk_bytes`, `bootstrap` report reference. Each attempt has `case`, one-based `number`, `status`, `classification`, `retry_of` (null or previous number), UTC RFC3339 `started_at`/`finished_at`, `exit` (integer or null if never started), `error` (string or null), `evidence` reference. |
| #380 aggregator/importer, `aggregate.json` | `schema_version`, `run` and `selection` references, `shards` (`id`, `status`, `attempts` reference or null), `status`, `passed`, `cases` (`name`, `shard_id`, `attempt_count`, `final_attempt`, `status`), `error` (string or null). |
| #380 local importer, `result.json` | Existing suite envelope: `tier`, `passed`, `error`, `cases`; each row retains native suite fields, including `name`, `passed`, `exit`, metrics and provenance. Add `remote` containing `schema_version`, `aggregate` reference, `tested_commit`, `base_commit`, `bundle_sha256`. Preserve `resumed_from`, `staged_from`, `postmortem_only` when present; never erase them to pass the gate. |

A reference is `{path, sha256}`. Paths are relative to the evidence root, use
forward slashes, and cannot be absolute, contain `..`, escape via links, or
collide under Windows case folding. Hashes are lowercase SHA-256 of exact file
bytes; do not reserialize before checking. Sizes are nonnegative integer bytes.
Git revisions are full lowercase 40-hex object IDs in this SHA-1 repository.
Archives reject path escapes and links, and must match the exhaustive inventory
after extraction. Part sizes/digests identify the encrypted archive bytes in both
origin and cache; verify them before decryption and extraction. A release
asset ID locates bytes; only the expected digest establishes their identity.

`run_id` is `gh:<repository>:<actions_run_id>:<actions_run_attempt>` and uniquely
names one invocation. Its immutable subject is the tuple (`tested_commit`,
`base_commit`, `tier`, bundle manifest SHA-256). The digest of `run.json` binds
the full configuration as well. Selection references that digest; attempts and
aggregation reference both run and selection digests. A GitHub rerun gets a new
run ID and complete evidence set; it cannot replace an earlier invocation's
files. Internal case retries stay in that invocation. No circular hashes:
inventory → bundle → run → selection → attempts → aggregate → local result.
Evidence files and bootstrap reports are leaves, owned by #378/#381, and may
keep their existing native formats.

## Source and base selection

Agents commit locally and do not push or open PRs. The maintainer publishes the
requested commit to a same-repository ref, then dispatches from the trusted
default-branch workflow with explicit `tested_commit`, `base_commit`, `tier`
(`smoke`, `land` or `full`), and pinned bundle reference. A maintainer may
explicitly authorize an agent to dispatch an already published commit; the issue
alone does not authorize publication. The source repository is public (maintainer
confirmation, 2026-09-19). Source upload, R2 and spot/self-hosted runners remain
alternatives outside v1; repository conversion is not a delivery prerequisite.

For manual dispatch, the operator chooses the exact comparison base, ordinarily
the task's merge base with local main when requesting publication. Both objects
must be fetchable from the same repository; the base must be an ancestor of the
tested commit. For `push` to `main`, tested commit is the event's `after`, and
base is its `before`, covering all commits in that push, not just `HEAD^`.
Resolve and fetch these once; never replace them with the branch's current tip.
Deletion, zero/missing base, unavailable history and non-ancestor force-push
bases fail planning with an explicit reason. A maintainer can dispatch again
with a valid ancestor; there is no silent smoke fallback. Reject equal base/head
for land. Smoke and scheduled full may compare equal commits because neither selects by the diff. Schedule runs pin the default-branch event SHA as both base and head; full selects every non-matrix registry case and refuses unsupported capabilities without reducing coverage.

#379 selects from the clean detached tested checkout using the explicit ancestor
base (`diff_mode: "ancestor-tree"`), including additions, deletions and both
paths of renames. It uses the tested revision's registry and affected rules,
plus the complete smoke set for land, honoring existing matrix exclusion and
documented sampling. #366 remains its correctness dependency. Never run the
default `-base main` on checked-out main: that can reduce a real push to smoke.
The current `ChangedFiles` uses a merge base and working-tree changes; the
ancestor requirement and a verified clean checkout make the remote comparison
equivalent without changing local semantics. Detect dirty/generated tracked
changes before planning and fail.

The first planner uses `algorithm: "sorted-round-robin-v1"`: sort selected case
names bytewise, choose min(case count, configured shard count) nonempty shards,
and assign name at index i to shard i modulo count. IDs are `s1`, `s2`, etc.
Every selected case occurs in exactly one shard; reasons and sampled areas are
retained. No cost estimate influences this version's assignment. Future cost
balancing must name a new algorithm and pin its cost input. Reject empty,
unknown, duplicated or omitted cases before starting runners.

## Planning command

From `go/` in a clean, detached checkout of `run.tested_commit`, run:

```text
go run ./internal/nativeaccept/cmd/acceptance plan -evidence <root> -run run.json -fetch
```

Capture stdout as `selection.json` only when the command exits zero. Diagnostics
use stderr; a rejected plan emits no partial selection. Keep the evidence root
outside tracked source. `-fetch` obtains the exact base/head objects from
`run.repository` and unshallows history; omit it only when complete comparison
history is already available. The dispatcher supplies the explicit event
identities in `run.json`: dispatch inputs, or push `before`/`after`. Planning
never substitutes a moving ref. Invoke the tested checkout's source as above;
a runner binary compiled from another revision is not a valid planner.

The command verifies provenance, duplicate JSON keys, limits, detached HEAD,
tracked and untracked cleanliness, both commit objects and base ancestry. It includes both
paths of renames and shares `landCases` and `affected.Select` with local tiers.
The #366 entry-point dependency is included in those affected rules.

Planner output adds optional case metadata: `fixture_ops` names declared Start
fixture operations, `roles` lists `bridge` and, where needed, `controller`, and
`mod_role` is `fixture` or `production`. Fixture rows require the complete default
fixture build, including common quiet/needs tools and fixtures called inside
case bodies; `fixture_ops` alone is not a complete build allowlist. Production
rows require a fixture-free build. An executor must support switching private
layouts for these roles or reject the complete plan before running it.

V1 includes both headless and rendered non-matrix cases. The planner records
`rendered` metadata; the native runner stops a kept headless process and launches
rendered cases through its ordinary windowed profile. Graphics/display support on
the hosted image is established by real frame assertions, not presumed absent or
present from its runner label. A graphics initialization or capture failure is a
failed case; never omit it from coverage. No paid GPU runner fallback is authorized.
Each shard's sum of declared case budgets times `max_attempts` must fit its
suite allowance. This is a necessary bound, not a prediction that boot and
cleanup will fit; executors enforce the actual deadline. No historical timings
are consulted, so untimed cases are assigned identically on every machine.

## Runner, storage and resource boundary

Initial target: public source repository, standard GitHub-hosted Windows x64,
`windows-2022`, with **age-encrypted release assets in the public source
repository** (maintainer choice, 2026-09-19). Only ciphertext is public; the age
identity remains a trusted-run secret. #377 pins the repository/release/asset
IDs during explicit maintainer publication; the example origin is synthetic.
See [bundle tooling and bootstrap](../remote-bundles.md). Each job gets one private game
layout and one suite worker. **Actions cache is the normal restore path**, keyed
`rimgovernor-bundle-v1-windows-x64-<bundle-manifest-sha256>`, without broad restore
prefixes. Cache the encrypted archive parts exactly as published at the pinned
origin, not the extracted tree. Cold misses download, verify and populate that
cache; hits verify the same digests. A corrupt hit fails validation and must be
replaced under a new bundle identity or removed before retry; never extract it.

Package one 7z archive, encrypt it using the [age v1 format](https://age-encryption.org/v1),
then split ciphertext into the ordered parts. Reassemble parts before decryption.
Use `encryption.format: "age-v1"` with an age recipient public key at packaging
and its private identity supplied as a trusted-run secret. `key_id` identifies
rotation without exposing the key; #377 owns encryption tooling and key setup,
#378 verifies decryption/inventory, #382 supplies the secret only at bootstrap.
Cache access itself is not private on the public repository: fork workflows can
read base-branch caches. Encryption preserves the planned Actions cache fast path
without making licensed files readable to cache consumers. Plaintext exists only
in the job's private layout and is never re-cached. Public dependency caches stay
separate. See
[cache access rules](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching).

The public encrypted origin needs no cross-repository credential. Use the
source repository's read-only token for authenticated release metadata/downloads
and API rate limits. Never pass it to branch builds or persist it in the checkout.
Only the explicit maintainer publication command needs release write access.
The bootstrap still gates trust before cache restore or identity access: public
ciphertext does not authorize unreviewed code to decrypt licensed inputs.

Public Actions logs and artifacts are public-facing outputs. #380 uploads an
allowlisted, sanitized evidence tree (reports, metrics, flight/log diagnostics and generated PNG frame captures),
never entire game/profile/work directories, credentials or licensed bytes. Raw
restricted diagnostics stay out of public uploads; if required, #377/#380 use
separately age-encrypted assets with a digest-bound reference and trusted retrieval.
Missing required diagnostics cannot be hidden by redaction: report incomplete
until the importer can retrieve them. The initial evidence fixtures require no
private diagnostic payloads. #377 inventories licensed inputs before packaging.

#382 accepts only maintainer-controlled dispatch and protected main pushes, using
a trusted workflow revision recorded as `workflow_commit`. No PR, fork or
`pull_request_target` trigger receives licensed inputs or credentials. Before
executing any selected source, validate same-repository provenance and maintainer
trust of the **exact tested commit**; merely copying fork code onto a local ref
or approving a workflow run does not establish code trust. Build and test code
can read downloaded game files even after credentials are removed. Maintainer
publication/dispatch therefore includes review of that commit; main pushes rely
on the repository's review/protection policy. Do not execute workflow definitions
from a task ref. Pin external actions by commit. A digest detects corruption,
not authorship: #380 also verifies artifact repository, workflow identity and run
ID via authenticated Actions metadata. Local imports verify public artifacts in
the same way; public readability does not establish trusted provenance.

These are conservative project limits, not purchased capacity:

| `limits` field | Initial maximum / behavior |
| --- | --- |
| `runner_label` | `windows-2022`; #378 records actual image and hardware, verifies headless game, .NET/Go/build tools and free disk before execution. |
| `shards`, `max_parallel`, `workers_per_shard` | 32 shards, up to 20 active shard jobs per run, 1 game worker per job. Independent workflow runs may overlap; GitHub enforces the account-wide runner capacity. |
| `job_timeout_minutes`, `suite_timeout_minutes` | 360 per shard job (the GitHub-hosted six-hour limit), 345 for its suite including retries; reserve 15 minutes for bootstrap, cleanup and upload. Planner and aggregation each capped at 10 minutes. Reject plans whose known case budgets cannot fit rather than dropping cases. |
| `max_attempts` | 2 per case; retries consume the same time allowance. |
| `artifact_retention_days`, `artifact_max_bytes` | 7 days, 1073741824 bytes total per run; preserve verdicts and failing diagnostics first, explicitly index any truncated optional evidence. Missing required evidence fails aggregation. Never upload game files to meet this cap. |
| `paid_usage_authorized` | false. Standard public hosted runners only; no larger runner, purchase or paid storage overage authorized. Verify repository visibility, storage allowance and applicable stop-usage controls before enabling triggers. If visibility changes, stop and revalidate capacity/billing; do not silently use paid runners. |

Thirty-two shards cap suite execution at 1440 runner-minutes, with up to 1920
shard job minutes plus 20 planner/aggregation minutes. This is an execution bound, not a
billing estimate or assurance the full land selection fits. #382 enforces the
bounds, #383 measures the actual selection and resources; raising limits needs
an explicit maintainer decision. Run the small smoke proof first.

Public documentation checked 2026-09-19 lists public standard Windows runners
as 4 CPUs, 16 GB RAM and 14 GB SSD; actual runner capacity must still be checked.
GitHub documents a six-hour hosted job maximum. These are service descriptions,
not verified capacity for this account: #378 must measure available disk after
checkout/extraction and probe the actual image; #382 must recheck account
concurrency and service limits before enabling the workflow.
See [runner specifications](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
and [Actions limits](https://docs.github.com/en/actions/reference/limits).

Standard hosted runner use is free for public repositories; larger runners are
not included in that choice. Storage still needs account-specific checks; do not
copy the epic's price or allowance estimates into provisioning. Budget alerts
alone do not stop usage. The maintainer verifies applicable billing controls at
activation; this contract has not inspected account billing. See [billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
and [stop-usage budgets](https://docs.github.com/en/billing/how-tos/set-up-budgets).

## Attempts, completeness and landing import

Attempt `status` is `passed`, `failed`, `cancelled` or `timed_out`; classification
is `none`, `infrastructure`, `assertion`, `unknown`, `cancelled` or `timeout`.
Success requires exit 0, native report `passed: true`, and required native
postconditions. A process exit or accepted order is insufficient. #381 owns a
versioned narrow allowlist in tested source: only positively identified
infrastructure failures can retry, once, fresh and restaged, in a recovered
private layout. Assertions, generic deadline/latency errors, unknown failures,
cancellations and exhausted time budgets never retry by text matching alone.
`retry_of` must name the immediately preceding failed eligible attempt. Retain
its complete evidence even when the retry passes. Do not silently inherit the
epic's superseded #349 retry proposal.

Local suites expose `retry_policy`, and each case row adds `attempts`,
`attempt_count`, nullable `final_attempt` and `disposition` (`passed`, `failed`,
`retried_passed`, `retried_failed`, `cancelled`, `timed_out`). Attempt records use
this contract's case/number/status/classification/retry/timestamp/exit/error
fields. `evidence` references the native result and `log` references the case
log, each with a SHA-256 and path relative to the suite output. A missing file
is null and cannot authorize retry; the remote exporter must reject missing
required evidence. Other diagnostics remain beside each native result. The
final row retains the existing output/provenance fields; wall and boot totals
include both attempts. The first output stays in its usual location; the second
uses `attempts/2/<area>/<case>`, after stopping the worker-owned game, with fresh
staging and checkpoint resume disabled. Resumed suites never retry.

The installed policy is `native-read-v1-empty`: there are no enabled production
classifiers. Retained supply failures contain observation-read timeouts alongside
unresolved gameplay attempts, and do not establish a transient read as the cause.
Neither nested diagnostics nor a caller-provided classification authorize retry.
A new rule needs captured causal evidence, a narrow classifier, negative tests for
writes/assertions/budgets/setup, and a new policy ID. Synthetic execution tests
exercise an eligible failure followed by a fresh pass without enabling a rule.
The remote envelope and aggregation projection remain owned by #378/#380/#382.

Aggregation runs even after shard failure. Shard `status` is `complete`,
`missing`, `cancelled` or `timed_out`; absent attempts references are null.
Case and aggregate status are `passed`, `failed` or `incomplete`. Missing cases
have `attempt_count: 0` and `final_attempt: null`. A shard can be complete with
failed cases. Unknown/extra shards, duplicate cases, gaps in attempt numbering,
mixed identities, corrupt artifacts, missing diagnostics or missing/cancelled
shards cannot pass. Incomplete takes precedence over failed. A final retry pass
counts only with its preceding eligible failure retained. `passed` is true iff
every planned shard is complete and every selected case passed; non-passing
aggregates have an explanatory error and nonzero aggregator exit status.

#380 downloads into a new local evidence directory, validates provenance,
digests, exact case/shard coverage and native reports, and emits the existing
suite-shaped `result.json` beside the untouched remote manifests. Use the
[aggregation and import commands](../testing/remote-evidence.md), then
`go run ./cmd/land -results <import-directory>/evidence` from the task's `go/`.
Smoke evidence satisfies the landing gate (#387); the nightly full tier owns
broader affected-area validation. Match the
tested task changes and inputs before the lane's normal main merge. Record that
association, then preserve it through the clean lane merge: unrelated main
movement, squash/rebase or a clean cherry-pick alone does not invalidate evidence.
Unmatched changes or changed relevant inputs require evidence covering them.

The current [gate](../../../go/cmd/land/gate.go) reads `passed`, `tier`, `cases`,
and the resume/stage/postmortem markers. It rejects failed, staged and
postmortem-only reports, but allows and names local resumed rows. Remote imports
also require authenticated artifact provenance, complete shard evidence and a
source match before the lane merges main. V1 remote runs are fresh/restaged; resumed
evidence must not be presented as a fresh remote pass. Do not merely rename
`aggregate.json` to `result.json` or trust its top-level boolean.

## Fixture review

The examples contain the six current smoke cases partitioned into two shards,
with synthetic native reports and one successful attempt each. Their manifest
references are real hashes of the checked-in fixture bytes; component/archive
identities and GitHub IDs are synthetic and cannot bootstrap a game. The
`suite-s1.json` / `suite-s2.json` projections use today's `-suite` array format;
the larger selection object is not accepted by that flag. A shard invokes
`acceptance suite -suite <projection> -workers 1 -timeout 45m -root <private-root>
-output <fresh-output> -rimgovernor <built-exe>`; do not combine `-suite` and
`-tier`. The importer restores the requested tier from verified run metadata.

Downstream fixtures should mutate these examples to prove rejection of missing
shards, mismatched digests/source/base, missing/extra cases, unapproved retries,
failed native reports, and staged/postmortem reports even when the aggregate
claims success. Add an eligible failure followed by a fresh pass to prove all
attempts survive import. #379 additionally proves multi-commit pushes, explicit
manual bases, equal/missing bases and deterministic assignment. #383 owns the
real complete land run and measurements; no native run validates this document.

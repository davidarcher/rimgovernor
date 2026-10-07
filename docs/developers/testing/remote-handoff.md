# Maintainer-to-agent remote acceptance handoff

[Agent runbook](../agent-runbook.md) · [Choose checks](choose-tests.md) ·
[Workflow operation](remote-workflow.md) · [Evidence import](remote-evidence.md)

The maintainer owns source publication, review of tested code, bundle publication,
the decryption identity and spending controls. Agents prepare exact inputs,
retrieve diagnostics and import evidence. A task or issue does not authorize a
push. Dispatch by an agent requires explicit maintainer authorization for the
published tested revision.

## Prepare the request

Commit the ready milestone and run `go run ./cmd/test` from `go/`. Record the
full tested commit, the reviewed workflow
commit, bundle manifest SHA-256, tier (smoke, nightly or cases) and shard count. Do not manually
trim the planner's case list.

Before dispatch, use a checkout of the tested commit and the
[planning command](../contracts/remote-acceptance.md#planning-command) to check
coverage and budgets. A local planning preview has placeholder Actions identity
and is not evidence; the workflow creates its own authenticated plan. Retain
the exact selected names, reasons and shard assignments. A rejected plan means
no run is ready: increasing shards may fix combined budgets, but cannot fit a
single case longer than the 345-minute suite allowance. Track that case or
planner issue instead of relabeling a reduced selection as land/full.

Ask the maintainer to publish the tested commit and base to the same repository,
review the exact tested tree (including build scripts), and dispatch or authorize
the following command. Publication is separate from the agent's local landing.
Use `tested_ref` to test a published branch or tag; the gate resolves its current tip
once. An explicit `tested_commit` overrides it. The workflow
revision comes from protected `main`; confirm it still matches the
reviewed revision before dispatch.

```text
gh workflow run remote-acceptance.yml --ref main -f tested_commit=<40-hex-tested> -f tier=smoke -f shards=<count>
gh workflow run remote-acceptance.yml --ref main -f tested_ref=<branch> -f tier=full -f shards=32
gh run list --workflow remote-acceptance.yml --event workflow_dispatch --limit 10
gh run view <run-id> --json url,headSha,event,status,conclusion,jobs
```

Identify the new run by its source inputs and attempt, not simply the newest
run in a shared repository. Record its URL in the issue. Use two shards for
smoke, one worker per shard, and at most 20 active shard jobs per run. Size land by
the planner's complete selection and budget checks, up to 32 shards. These are
bounded operational defaults, not a measured optimum. Local worker tuning
remains [#271](https://github.com/davidarcher/rimgovernor/issues/271).

## Retrieve, diagnose and land

```text
gh api repos/davidarcher/rimgovernor/actions/runs/<run-id>/artifacts
gh run view <run-id> --attempt <attempt> --log-failed
gh run download <run-id> --name acceptance-<run-id>-<attempt>-verdict --dir <diagnostic-directory>
```

The verdict artifact contains the complete sanitized evidence tree. Shard
artifacts and Actions logs remain useful if aggregation did not finish. Download
within seven days. A manual download is diagnostic material; only authenticated
import establishes trusted evidence. Record artifact ID, digest and size, then
follow [the import command and trust pins](remote-evidence.md#import-an-authenticated-actions-artifact)
in the matching clean task checkout. Keep `actions-artifact.zip` beside its
`evidence/` directory. `cmd/land` does not read it; the import is for diagnosis.

Read `selection.json`, `aggregate.json`, each shard's `attempts.json`, and the
referenced native reports. A green bootstrap or accepted order is not a native
postcondition. Native failure, infrastructure failure, and missing/cancelled
output are distinct; none may be converted to a pass. Preserve all attempts.
Production retries are disabled (`native-read-v1-empty`). If an authorized
rerun is needed, choose **Re-run all jobs** so one attempt contains its complete
plan and shards. Do not borrow green rows from a previous attempt.

For each rollout, record workflow/tested commits, bundle digest, exact
cases, attempts, per-case and per-shard durations, cache hit, restore/extract/build
times, observed job overlap, artifact sizes and Actions timing in the issue.
`gh api repos/davidarcher/rimgovernor/actions/runs/<run-id>/timing` reports billed
usage; a zero Windows total is not proof of account-wide storage cost or a
spending cap. The maintainer confirms those separately. Compare cold and warm
runs of the same source/tier; queue time and build time can dominate cache gains.
Reuse successful evidence instead of rerunning because main moved.

## Cancel and rotate

Cancel only the task-owned run with `gh run cancel <run-id>` when authorized.
The workflow's cleanup stops its own processes and removes the identity;
hosted runner disposal removes private layouts. A hard kill can prevent cleanup
or upload, so check terminal job conclusions and retain whatever diagnostics
exist. Missing/cancelled shards stay incomplete, even if some case reports pass.
For a cancellation demonstration, retain the run URL, job conclusions and
import/aggregate refusal. Synthetic cancellation checks prove gate behavior,
not that hosted cleanup executed.

For bundle rotation, the maintainer publishes newly encrypted parts, inventory
and manifest, updates the environment identity and repository asset/digest
variables together while dispatch is quiescent, and provides the new digest to
agents. Follow [bundle publication and cache verification](../remote-bundles.md).
The new digest has a separate cache key; do not broaden restore prefixes or
overwrite a pinned manifest. Preserve old assets/identity access as needed for
in-flight runs, then retire only the superseded cache/release material under
the maintainer's retention policy. Import trust pins must identify the bundle
and workflow actually reviewed for that run.

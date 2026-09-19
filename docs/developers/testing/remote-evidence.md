# Aggregate and import remote acceptance evidence

[Manifest contract](../contracts/remote-acceptance.md) · [Choose checks](choose-tests.md)

Run commands from `go/`. `cmd/remoteaccept` never publishes source, dispatches a
workflow, uploads artifacts or downloads the licensed game bundle. Workflow
collection and upload belong to #382; the first real remote smoke import is
part of the #383 rollout validation.

## Aggregate a collected run

Keep the original `run.json`, `selection.json`, bundle manifest, bootstrap
reports, attempts manifests and native evidence under one root. References use
paths relative to that root, forward slashes and SHA-256 of exact bytes. When
collecting suite output into a shard subdirectory, the exporter must normalize
references before hashing; absolute runner paths are not portable evidence.

Create `shards.json` as an array of the contract's `{id, status, attempts}` rows.
Record every planned job, including missing, cancelled and timed-out jobs;
`attempts` is null when the job produced none. Do not infer job success from
the presence of an uploaded file.

```text
go run ./cmd/remoteaccept aggregate -root <evidence-root> -shards shards.json
```

The command writes `aggregate.json` and the suite-shaped `result.json`. A failed
or incomplete run exits nonzero. It recomputes case coverage and native verdicts;
a supplied top-level success flag cannot override a failed case. Missing files,
duplicate keys, unknown versions/statuses, incorrect digests, mixed identities
and unexpected shard/case assignments fail closed. The selection must follow
`sorted-round-robin-v1`. Source identity and authenticated planner provenance
are checked on import, where the tested Git objects are available.

An attempt's `evidence` can reference a native case report (`case` or `name`) or
an existing suite report containing its named row. Suite row metrics and other
native fields are preserved. Every attempt and original file remains untouched;
projected rows add `remote_evidence` and `remote_attempts` links. The aggregate's
per-case summary includes attempt count, final attempt and disposition.
An optional attempt `log` reference and native `evidence_files` array of
references are verified too. Native `log`, `output` and `flight_recorder` paths,
when supplied, must exist inside the collected root. Exporters must include
required diagnostic references; absent optional fields do not assert that a
diagnostic was captured. Resumed, staged and postmortem-only rows retain their
markers and cannot become a fresh remote pass.

An eligible infrastructure failure followed by a pass can be represented and
checked without losing its first evidence. Import additionally enforces the
current production policy, `native-read-v1-empty` (#381): no production retry
is authorized yet. Enabling a classifier requires coordinated executor and
importer policy support; a manifest classification is not authorization.

Upload only the sanitized diagnostic tree. The importer accepts generated PNG frame captures (decoded and dimension-bounded) and regular JSON,
JSONL, log, text and Markdown files, rejects links, Windows path aliases,
case collisions and path escapes, and bounds the compressed and expanded
artifact to 1 GiB each. This extension allowlist is not a content sanitizer:
the trusted workflow must remove secrets and licensed contents before upload.
Keep all attempt logs, metrics and flight recorder segments in the artifact;
do not upload a whole game, profile or worker directory.

## Import an authenticated Actions artifact

The maintainer establishes trust independently of the artifact. Set these local
Git configuration values to the reviewed source repository, workflow path,
exact workflow revision and bundle manifest digest:

```text
git config --local rimgovernor.acceptancerepository davidarcher/rimgovernor
git config --local rimgovernor.acceptanceworkflow .github/workflows/<trusted-workflow>.yml
git config --local rimgovernor.acceptanceworkflowCommit <40-hex-workflow-commit>
git config --local rimgovernor.acceptancebundleSHA256 <64-hex-bundle-manifest-digest>
go run ./cmd/remoteaccept import -repo .. -run <actions-run-id> -attempt 1 -artifact <artifact-id> -output <new-directory>
go run ./cmd/land -results <new-directory>/evidence
```

Use workflow path `.github/workflows/remote-acceptance.yml`; see [activation and diagnostics](remote-workflow.md). GitHub CLI must be
authenticated and the tested/base Git objects must already exist locally.
The importer verifies same-repository Actions metadata, exact workflow revision,
run attempt, artifact identity and the GitHub artifact digest before extraction.
Cancelled, timed-out, expired, missing or unverifiable artifacts cannot pass.
The manifest run identity, source/base and bundle must agree with these pins.
The immutable archive is retained as `actions-artifact.zip`; original manifests
and diagnostics remain in `evidence/`, beside the imported `result.json`.
Failed imports retain downloaded evidence for diagnosis and exit nonzero.

Landing reauthenticates and replays that archive, compares the local evidence
and report against it, and binds it to the clean task before merging main.
It verifies the selection's changed-file list against the tested base/source
diff. Identical source trees remain valid across rewritten commit IDs; a clean
merge of tested source with main also remains valid. Additional task edits or
dirty tracked files need matching evidence. The normal lane merge does not
trigger another check run. Keep the archive and `evidence/` together and import
before artifact expiry; offline or expired metadata does not establish trust.

Smoke evidence is accepted for landing under #387. The nightly full tier owns
broader affected-area validation. Local resumed suites retain their existing
landing semantics; remote v1 evidence remains fresh and restaged.

## Fast validation

`go run ./cmd/test` covers synthetic complete imports, retry-history retention,
case-failure injection, missing/duplicate/cancelled evidence, unsafe archives,
wrong provenance/source, local tampering and clean main-merge reuse. Fixtures
marked `fixture_only` are rejected by import; these checks prove tooling behavior,
not native pawn outcomes. #383 records the real remote smoke report and later
rollout measurements.

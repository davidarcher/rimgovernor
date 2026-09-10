# Go migration coverage

These inventories describe the Python comparison surface for G01. They are not
Go capability declarations. `source_revision` records the Python revision inspected;
`status` remains pending until the owning chunk's behavioral acceptance passes.

- `domain-inventory.json`: module classification, semantic/action/completion kinds
  and native tool call sites.
- `interface-inventory.json`: HTTP/events, configuration, launchers and media.
- `state-inventory.json`: SQLite, checkpoint and recovery boundaries.
- `fixtures/`: small sanitized comparison cases with source provenance. Synthetic
  fixture evidence does not establish a native outcome or model interpretation.
  `state-baseline.json` retains seven representative cases;
  `serialization-baseline.json` retains exact Python JSON bytes/signatures and
  uncertain progress. Its self-test rejects a deliberately changed signature.

Each row identifies its source, target Go package, owning G01 chunk, existing
fixture checks and native scenario references. Empty evidence lists preserve an
uncovered acceptance requirement; they do not waive it. Tooling classifications
identify code that may remain Python after the production cutover.

Run `python scripts/check_go_coverage.py` from the repository root to validate
cross-inventory structure and the source-specific drift checks. See the
[rewrite sequence](../docs/BACKLOG.md#g01--go-controller-rewrite) for dependencies
and the [test selection guide](../docs/how-to/choose-tests.md) for acceptance scope.

## Timing baseline

The uncontended Python baseline is pending. Measurements alongside another native
scenario cannot establish this baseline. Coordinate an uncontended window without
stopping a workload owned by another task.

Before G01.12, use the existing [throughput procedure](../docs/how-to/measure-throughput.md)
with isolated licensed inputs and identical game, model, storage and rendering
settings. Retain source/image/input hashes and raw reports under `.rimgovernor/`.
Record numeric regression budgets before examining Go results. No performance or
production cutover acceptance is implied by inventory validation.

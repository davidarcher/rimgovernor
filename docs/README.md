# RimGovernor docs

## For players

[Player guide](players/README.md): set up a colony, use the dashboard, direct
automation and save your session.

- [Setup](players/setup.md) and [launch options](players/launch.md)
- [Dashboard and controls](players/controls.md)
- [Save and resume](players/save-and-resume.md)

## For developers

[Developer guide](developers/README.md): find the owner of a change, understand
its contracts and run the relevant checks.

- [Architecture](developers/architecture/overview.md) and [source map](developers/source-map.md)
- [Development workflow](developers/development-process.md) and the
  [agent runbook](developers/agent-runbook.md) (shared machine, private game copy, running harnesses)
- [Subsystem contracts](developers/contracts/README.md) and
  [wire contracts](../contracts/README.md)
- [Remote acceptance contract](developers/contracts/remote-acceptance.md), example manifests and
  [evidence aggregation/import](developers/testing/remote-evidence.md)
- [Generated wire contracts](../contracts/schema-generation.md)
- [Choose tests](developers/testing/choose-tests.md) and
  [measure throughput](developers/testing/measure-throughput.md) (flight recorder, `rimgovernor phases`, `rimgovernor trace`, speed matrix, the case timeline page)
- [Go controller development](../go/README.md), including its testing pyramid;
  native acceptance tooling is tracked in
  [issue #38](https://github.com/davidarcher/rimgovernor/issues/38)
- [Backlog issues](https://github.com/davidarcher/rimgovernor/issues): unfinished
  features and acceptance, labeled by priority (`priority:P0`/`P1`/`P2`)
  or area (`area:G01`/`N01`/`simplify`/`tooling`)

Keep docs close to the reader's task. Explain current behavior, give the commands
or contracts they need, and link to detail. Put unfinished work in the backlog
and implementation history in commits.

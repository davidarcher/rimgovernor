# Documentation

## Play

Start with the [player guide](players/README.md).

| Task | Guide |
| --- | --- |
| Install and prepare the game | [Setup](players/setup.md) |
| Start a save or generate a colony | [Launch](players/launch.md) |
| Understand the launcher and take control | [Controls](players/controls.md) |
| Save, stop and resume | [Save and resume](players/save-and-resume.md) |

## Develop

Start with the [architecture](developers/architecture/overview.md) and
[source map](developers/source-map.md). The [developer guide](developers/README.md)
links the component guides; the [glossary](developers/glossary.md) defines terms.

| Task | Guide |
| --- | --- |
| Make and land a change | [Workflow](developers/development-process.md), [agent rules](../AGENTS.md) |
| Build or run the Go controller | [Go module](../go/README.md) |
| Work safely on the shared machine | [Runbook](developers/agent-runbook.md) |
| Choose evidence for a change | [Choose tests](developers/testing/choose-tests.md) |
| Write or iterate on a native case | [Acceptance guide](developers/testing/acceptance-guide.md) |
| Replay planner decisions | [Colony snapshots](developers/testing/colony-snapshots.md) |
| Measure performance | [Throughput](developers/testing/measure-throughput.md), [hazard bounds](developers/architecture/hazard-detection-bounds.md) |
| Improve expert play | [Architecture roadmap](developers/architecture/expert-play-assessment.md) |
| Plan colony rooms and containment | [Facilities](developers/architecture/facilities.md) |
| Change a subsystem | [Behavior contracts](developers/contracts/README.md) |
| Plan and reconcile trade missions | [Controllable trade](developers/contracts/controllable-trade.md) |
| Preserve or reconsider issued work | [Method continuation](developers/contracts/method-continuation.md) |
| Change the native boundary | [Wire contracts](../contracts/README.md), [schema generation](../contracts/schema-generation.md) |

## Gameplay and remote evidence

- [Shelter coverage](developers/testing/shelter-coverage.md): which check proves each claim.
- [Colony review](developers/testing/colony-review.md): nightly colony runs and screenshots.
- [Remote handoff](developers/testing/remote-handoff.md): prepare, dispatch, diagnose and import.
- [Remote Windows workflow](developers/testing/remote-workflow.md) and
  [encrypted bundles](developers/remote-bundles.md): runner setup and artifacts.
- [Remote acceptance contract](developers/contracts/remote-acceptance.md) and
  [evidence aggregation](developers/testing/remote-evidence.md): manifests and verdicts.

[GitHub issues](https://github.com/davidarcher/rimgovernor/issues) hold unfinished
work and decisions. Documentation describes current behavior and explicit
proposals; Git and result artifacts retain implementation history and run evidence.

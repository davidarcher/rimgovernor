# RimBot documentation

RimBot connects a deterministic colony controller and local-model player chat to
RimWorld. Both use the same durable plan and native execution path. Choose a starting
point by what you want to do or understand.

| Your question | Start here |
| --- | --- |
| Can I try the development workflow without installing the game? | [Tutorial: your first Docker test](tutorials/first-docker-test.md) |
| How do I run, test or recover something? | [How-to guides](how-to/README.md) |
| Which module, contract or artifact do I need? | [Technical reference](reference/README.md) |
| How do the internals fit together, and why? | [Explanation: the system overview](explanation/overview.md) |
| What is unfinished? | [Project backlog](BACKLOG.md) |

## Explore the internals

Start with the overview, then follow the topic that interests you. Each explanation
introduces the idea before linking to the exact contracts and relevant source.

1. [System overview](explanation/overview.md): who owns decisions, execution and simulation.
2. [The control loop](explanation/control-loop.md): observations, priorities and verified progress.
3. [Plans and Hands](explanation/plans-and-hands.md): how a player request becomes ordinary game work.
4. [Space and resources](explanation/space-and-resources.md): why valid plans need fresh native checks.
5. [Sessions and recovery](explanation/sessions-and-recovery.md): identity, interruptions and paired saves.
6. [The dashboard and game view](explanation/dashboard.md): colonist dossiers, presentation, video and player control.
7. [Testing and evidence](explanation/testing.md): what each kind of test establishes.

[Needs-driven research](reference/research.md) defines queue and unlock verification.

## Find an operational guide

- [Set up a Windows checkout](how-to/setup.md) and [launch a prepared colony](how-to/launch.md).
- [Choose checks](how-to/choose-tests.md), [run local checks](how-to/local-checks.md),
  or [run focused/full Docker controller checks](how-to/docker-checks.md).
- [Reuse a headless game between execution cases](how-to/headless-probes.md#reuse-one-game-between-execution-cases).
- [Prepare/cache Linux inputs](how-to/docker-inputs.md) and [run native Docker acceptance](how-to/docker-native.md).
- [Verify spatial construction, access and reuse](how-to/spatial-acceptance.md).
- [Verify environmental observations and cooking fallback](how-to/docker-native.md#verify-environmental-observation-and-cooking-fallback).
- [Verify compound disaster recovery](how-to/docker-native.md#verify-compound-disaster-recovery).
- [Evaluate visual review and evidence recall](how-to/visual-reviews.md).
- [Verify mood relief](how-to/mood-relief.md) against its [native recovery contracts](reference/mood-control.md).

- [Verify medical care](how-to/medical-care-acceptance.md) against its [health and surgery contracts](reference/medical-care.md).
- [Verify zone, bill and UI player actions](how-to/player-actions.md) against their [coverage contracts](reference/player-actions.md).
- [Verify waste hauling and burial](how-to/waste-management.md) against their [containment contracts](reference/waste-management.md).
- [Population commitments](reference/population-contracts.md) distinguish candidates, native custody and admitted colonists.
- [Verify population outcomes in Docker](how-to/population-acceptance.md).
- [Verify animal husbandry](how-to/husbandry-acceptance.md) against its [management contracts](reference/husbandry-contracts.md).
- [Save and resume](how-to/save-and-resume.md) or [inspect a failed run](how-to/inspect-failure.md).
- [Verify policy trades](how-to/trade-acceptance.md) against actual goods and silver.
- [Verify equipment upkeep](how-to/equipment-upkeep.md) and its [native contracts](reference/equipment-upkeep.md).

The [how-to index](how-to/README.md) also covers domain acceptance, campaigns, model
comparisons, video and persistence audits.

## About these docs

The structure follows [Divio's documentation
system](https://docs.divio.com/documentation-system/): tutorials teach through a
concrete exercise, how-to guides solve a task, reference defines current behavior, and
explanation develops understanding. The backlog is the separate queue for unfinished
work, not a description of available features.

Contributor rules live in [AGENTS.md](../AGENTS.md). Add new material to its topic page
and link across categories; keep procedures out of conceptual explanations and exact
contract details out of introductory lessons. [ARCHITECTURE.md](ARCHITECTURE.md) and
[TESTING.md](TESTING.md) remain short entry points for existing links.

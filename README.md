# RimBot

A local RimWorld colony controller with a React dashboard. Deterministic systems
handle routine colony needs; a local model interprets explicit player chat and
offers advice. Both use one durable plan and Hands executor through
GABS/RimBridgeServer and the RimBot colony bridge companion. RimWorld owns the
simulation and ordinary game rules.

## Start here

| You want to… | Read |
| --- | --- |
| Learn the development workflow without game files | [Your first Docker test](docs/tutorials/first-docker-test.md) |
| Set up or launch the application | [Windows setup](docs/how-to/setup.md) · [Launch a prepared colony](docs/how-to/launch.md) |
| Understand the internals | [System overview](docs/explanation/overview.md), then [plans and Hands](docs/explanation/plans-and-hands.md) |
| Find a command, contract or module | [How-to guides](docs/how-to/README.md) · [Technical reference](docs/reference/README.md) |
| See remaining work | [Backlog](docs/BACKLOG.md) |

The [documentation home](docs/README.md) offers a reading path through the
internals and separates tutorials, how-to guides, reference and explanation.

## Launch an existing setup

```powershell
.\launch.cmd
```

The dashboard opens at [localhost:8787](http://127.0.0.1:8787) in Manual mode.
The launcher starts the prepared isolated colony or reuses a running native
bridge session. A clean checkout first needs the licensed game, native mods,
GABS and the prepared fixture described in [setup](docs/how-to/setup.md).
Autopilot requires no inference; player chat needs the configured local model
loaded in LM Studio.

## Work on the project

Follow [AGENTS.md](AGENTS.md), then [choose checks](docs/how-to/choose-tests.md)
for the change. To run Linux controller tests without a local project environment:

```powershell
python scripts/container_checks.py --workers 1 --image rimbot-checks:my-task --output .rimbot/docker-checks-01
```

Use Python 3.12+, a running Linux Docker daemon and a new output directory.
[Docker instructions](docs/how-to/docker-checks.md) cover artifacts and focused
tests; [native Docker acceptance](docs/how-to/docker-native.md) runs actual games.

Native receipts describe accepted orders, not completed pawn labor. The
[testing explanation](docs/explanation/testing.md) defines the evidence boundaries.
Source attribution and licenses remain in [THIRD_PARTY.md](THIRD_PARTY.md) and the
integration provenance files. RIMAPI is not a supported runtime backend.

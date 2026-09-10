# Verify animal husbandry

[Documentation](../README.md) · [Husbandry contracts](../reference/husbandry-contracts.md)

Use an isolated worktree and the [prepared Linux inputs](docker-inputs.md). Compile
the companion with `-p:HusbandryFixture=true` and the Linux reference properties
from that guide, then copy its DLL and matching identity assembly into private mod
inputs. The fixture is excluded from normal builds and the model gameplay gateway.
Do not replace DLLs in a running game or mutate inputs while staging.

Run one isolated worker with a fresh output directory and task-specific image tag:

```powershell
python scripts/container_husbandry_acceptance.py --game <linux-game> --mods <private-fixture-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimbot-worker:my-husbandry-task --output .rimbot/husbandry-01
```

Replace placeholders with absolute paths. The runner builds and pins the worker
image, mounts inputs read-only, stages a private game and owns only its uniquely
named container. Use `--no-build` only with an unchanged task image. The run timeout
defaults to 1,200 seconds, independently of the image build. No model server, host
port or display is required.

The fixture seeds an enclosure, feed, a handler, full product comps, a near-term
pregnancy and one remaining training step. None of those initial conditions count
as completed outcomes. The probe submits the training setting through a player herd
goal and Hands, then advances bounded ordinary native tick windows. It requires a
newborn with maternal identity, a subsequent pregnancy, learned training, milk and
wool stock increases alongside spent product comps, and increased animal food level
with consumed feed. It separately verifies suitable enclosure membership, pregnancy
protection and rejection of stale animal settings.

Require exit zero and both `result.json` and `run/husbandry-result.json` with
`passed: true`, including successful owned-container cleanup. The native report
retains setup, each assertion and full sampled native observations; the worker also
retains input hashes, source logs and staging evidence. Preserve failures, correct
the cause and use a fresh output directory. Compile production DLLs without the
fixture flag before release.

Run the [Docker controller checks](docker-checks.md) as well. Their husbandry cases
cover unknown feed, population and breeder protection, player authorization,
pending removals, seasonal reserves, handler availability and feed-goal cancellation.
Neither those fixtures nor this bounded native run certify local-model interpretation
or sustained seasonal survival; broader survival coverage belongs to B04.

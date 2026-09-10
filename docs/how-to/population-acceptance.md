# Verify native population outcomes

[Documentation](../README.md) · [Population contracts](../reference/population-contracts.md)

Use an isolated worktree and the [prepared Linux inputs](docker-inputs.md). Compile
the companion with `-p:PopulationFixture=true` and the Linux reference properties
shown in that guide. Copy the resulting companion and identity DLLs into a new
private mod snapshot before launching. Production builds omit `PopulationFixture`.

From the task worktree, run:

```powershell
python scripts/container_population_acceptance.py --game <linux-game> --mods <private-task-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimgovernor-worker:population-task --output .rimgovernor/population-01
```

The fixture prepares a prison, spare housing, food, medical supplies, a downed
hostile candidate and an injured non-hostile visitor. Setup is not gameplay
acceptance. The probe submits explicit policy and individual decisions through
the shared goals and Hands, then observes ordinary capture, rescue, feeding,
tending, recruitment and integration. It also checks over-capacity refusal and
stale prisoner-setting comparison. It never sets faction membership to complete
recruitment or calls model inference.

Require exit code zero, `result.json` with `passed: true` and successful cleanup,
and every case in `run/population.json`. Inspect the retained native observations,
goal progress and last blocker, plus `container.log` and `run/HeadlessPlayer.log`
(`run/Player.log` with `--display xvfb`) on failure.
The runner retains its probe copy, image ID and input hashes. Every rerun requires
a new output directory. Use `--no-build` only with an unchanged controller image;
new native DLLs still require a separate private mod snapshot.

This bounded fixture does not establish every faction, ideology, dietary need,
multi-map transfer or prolonged population sustainability. Unsupported native
steps remain explicit blockers. Ordinary pawn outcomes and local-model command
interpretation are separate acceptance scopes.

The default headless scenario uses the native `SpaceRefugee_Clothed` pawn kind,
whose definition starts with zero recruitment resistance. It still requires ordinary
warden recruitment and observed admission; no success probability is changed.
Use `--candidate-kind Villager` for native resistance reduction and allow a longer
`--seconds` budget. A decrease in resistance is evidence of warden progress, not
completed recruitment. Both kinds must meet every final assertion to pass.

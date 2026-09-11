# Verify resource production budgets

[Documentation](../../README.md)

Verify actual native acquisition, production and ingredient-policy effects using
ordinary pawn work.

[Native test prerequisites](README.md#native-test-prerequisites).

`scripts/resource_policy_acceptance.py --source-root <ordinary-prepared-root> --output
<fresh-directory>` creates a normal crafting spot and a native bill, then verifies
stopped production, one actual pawn-produced output, exact input consumption and a
reserve preventing another cycle. It also records native sources for the named resource
targets. Install both the current observation and identity assemblies plus the current
headless companion before launching; restore originals only after every game closes.
This focused probe does not certify unavailable industrial recipes, every material
alternative or sustained production.

With `--acquisition`, the resource policy probe also requires pawn-produced steel,
components and herbal medicine from observed normal mining/harvest sources. It compiles
native target work types through the shared work allocator and records assignment
receipts plus actual stock increases; designation receipts alone fail.

## Material substitution

`scripts/resource_substitution_acceptance.py --checkpoint <paired-manifest> --output
<new-directory>` requires a native checkpoint with observed wood and steel. It reserves
all available wood, requests a wall with WoodLog/Steel alternatives, and requires an
ordinarily completed native Steel wall, actual steel consumption and the preserved wood
floor. The checkpoint remains immutable.

## Fuel prerequisites

`scripts/resource_fuel_acceptance.py --source-root <ordinary-industrial-start> --output
<new-directory>` requires normal construction of a research bench, ordinary
BiofuelRefining research, an ordinarily built generator and refinery, then actual
chemfuel from the shared resource target bill. Missing prerequisites must be reported
before the new infrastructure and reconsidered afterward. The wall-clock limit is an
acceptance bound, not a simulation or research shortcut.

## Capacity and lease variants

Add `--capacity` to the substitution probe to observe an existing bill covering a
resource target, explicitly reduce that fixture bill's target, and require a new shared
target bill plus actual additional output. The original bill settings must remain
unchanged when capacity is added. Add `--lease` with `--capacity` to require an unissued
native construction budget to stop production during supervised ticks, then actual
production and exact ingredient consumption under ordinary Manual play after the lease
ends. The fixture settles pending controller reviews before starting the Manual clock
and records native time, pawn, bill and stock evidence so a pause cannot be mistaken for
a production-policy refusal.

## Long-running fuel acceptance

Fuel acceptance uses ordinary food acquisition/cooking and two ordinary research benches
so prerequisite research shares the colony's real labor and food budget. Research
progress checkpoints and an optional unchanged native autosave input preserve real work
across disposable test runs; neither supplies research points. Use `--checkpoint
<paired-manifest>` to preserve both native research and controller ownership. Completed
explicit fixture setup orders are archived through normal plan revisions; their verified
receipts remain in the paired controller database. Force-paused research dialogs are
captured with UI targets and a new paired checkpoint before cleanup, preserving the
native prerequisite outcome for review. Use `--production-resume --checkpoint
<paired-manifest>` to continue an existing refinery and bill without repeating
prerequisite setup. The probe records actual pawn jobs and stock through production. An
observed supported animal threat can hand control to the shared deterministic defense
method, then return to Manual after native threat clearance. Native guards remain active
throughout.

The named-resource matrix covers actual steel, component, herbal-medicine and wood
acquisition, plus chemfuel production. Industrial-medicine target resolution does not
certify industrial-medicine manufacturing or unavailable prerequisites.

## Related reading

[Testing](README.md) · [Backlog](../../BACKLOG.md)

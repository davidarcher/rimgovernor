# Explosive projectile causality checks

Build `NativeExplosiveCausalityTests.csproj` with `RimWorldManagedDir` and
`HarmonyAssembly` pointing to private licensed inputs. Run its net472 executable
with the private Bridge DLL path, Runtime directory, SDK directory, game managed
directory and Harmony directory, in that order.

The executable loads actual game, Harmony and production assemblies. It verifies
native IL call counts and the exact 36-argument explosion factory signature,
executes the production transpilers, and checks complete forwarding. The real
`GenExplosion.DoExplosion` body runs with controlled spawn, visibility and
`StartExplosion` effects. A test transpiler records factory arguments without
adding another factory prefix. Native shield-impact branching and the damage
worker's ignored-target early return execute directly.

Tests cover exact projectile-to-explosion object lineage, delayed damage after
projectile destruction/job cleanup, worker and load identity, Manual revocation,
nested factories/damage, notification and rectangle-trigger style side damage,
partial spawn/start/cell failures, capacity and later independent evidence. Actual
foreign Harmony prefixes exercise earlier factory recursion, `ref DamageInfo`
instigator/definition replacement and `__args` replacement. Those ambiguous hook
sets must fail closed and recover after removal. All ten required shared and
explosion hook registrations are removed and repaired independently, checking
unique registration counts, native forwarding, cleanup and later valid evidence.

This is not gameplay acceptance. Launch and damage forwarding use controlled
native-call replacements, not projectile simulation or calculated damage. Spawn,
visibility and start effects are also controlled; native explosion scheduling,
geometry, shield simulation and pawn outcomes require separate Docker acceptance.
Synthetic native objects receive only required fields. Capacity tests prefill
private dictionaries to exercise the real bounds. No attribution method is
replaced, and no game, listener, installed DLL or save is changed. Reflection-safe
instance-field stores avoid loading graphics-only projectile initialization.

# Direct-bullet causality checks

Build `NativeRangedCausalityTests.csproj` with `RimWorldManagedDir` and
`HarmonyAssembly` pointing to the private licensed game and Harmony inputs. Run
its net472 executable with the private Bridge DLL path, Runtime directory, SDK
directory, game managed directory and Harmony directory, in that order.

The executable loads the actual production adapter, game and Harmony assemblies.
It reads original native IL, runs the production transpilers and verifies five
launch sites and two direct damage sites. `NotifyImpact` remains outside those
wrappers. Missing/extra calls or an exception boundary refuse the rewrite; branch
labels survive the inserted argument loads. Every required live patch is removed
and repaired independently, with exact registration counts and a positive wrapper
call after repair. Adjacent melee damage and verb finalizers are also removed:
actual Harmony dispatch must keep forwarding damage without leaving an unpaired
scope, then recover valid attribution with exactly one of each required hook.

Real wrappers and the real Harmony damage prefix/finalizer run against synthetic
native objects. Test-only prefixes replace the forwarded native Launch and
TakeDamage implementations, capture arguments, and supply controlled damage
results or exceptions. Tests cover exact projectile object/ID and job lineage,
pooled job reuse, shield/foreign-target refusal, actual native Manual revocation,
notification-style side damage, nesting, exception cleanup, bounded storage and
sticky tracking loss without erasing independent valid-flight evidence.

This is not gameplay acceptance. No projectile simulation, native damage amount,
shield simulation, pawn combat or outcome receipt is proved here. Native objects
receive only the required instance fields; direct field stores avoid eagerly
loading Projectile's graphics-only static initializer. Startup flags suppress
engine-only definition warnings. No attribution method is replaced. Capacity
checks prefill private storage with synthetic rows to exercise its real boundary.
All changes exist only in the test process: no game, listener, installed DLL or
saved state is touched. Real ranged combat requires separate Docker acceptance.

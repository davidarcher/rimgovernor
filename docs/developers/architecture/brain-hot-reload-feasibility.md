# In-process brain: hot reload feasibility

Question: once the Go brain is ported to C# and runs inside the game process
(epic #3043), can the bulk of it be reloaded in the running game without
restarting?

**Verdict: yes, with a collectible `AssemblyLoadContext` around the brain
assembly. It needs a new loader; nothing in the host does this today.**
This note is static analysis of the repo plus known .NET behavior. No prototype
has been run in the game.

## Runtime facts

- RimWorld 1.7 runs on the Waylith engine under CoreCLR, net10
  (`THIRD_PARTY.md`). Every RimGovernor assembly targets net10.0.
- `AppDomain`s are not an option: .NET Core has one domain and
  `AppDomain.CreateDomain` throws. The in-process isolation and unload unit is a
  collectible `AssemblyLoadContext` (ALC).
- Because the brain stays in the game process, it calls `Verse` and the Bridge
  directly. There is no marshalling boundary to pay for.

## How code loads today

| Piece | What it does | Reload consequence |
| --- | --- | --- |
| `RimBridgeExtensionDiscovery` | Scans this mod's `BridgeTools/` folders and loads each DLL with `Assembly.LoadFile`. Resolves references through an `AppDomain.AssemblyResolve` handler that prefers any already-loaded assembly with the same simple name. | `LoadFile` gives each file an anonymous, non-collectible context, so these assemblies can never be unloaded. The resolver's by-simple-name rule is the right sharing policy to reuse for contracts. |
| `RimBridgeCapabilities.Initialize` | Runs once (`if (Registry != null) return`), registers providers, and keeps `ExtensionTools` and `LegacyToolExecution` pointing at them. | One-shot. Reload needs re-registration. |
| `CapabilityRegistry` | `RegisterProvider` only; no unregister or replace. `Gab/Tools/ToolRegistry` does have `UnregisterTool`. | Needs a provider-level replace/remove, or the brain registers through one stable provider that forwards. |
| `LoadedAssembliesByPath`, `DiagnosticsByAssemblyPath`, `AssemblyPathsByProviderId` | Static dictionaries holding `Assembly`, and via the tools `Type` and `MethodInfo`. | Each is a strong root into the loaded assembly. |
| `RimGovernor.Runtime.dll` | Loaded early by the game for Verse component discovery. Holds 14 `GameComponent` and `MapComponent` subclasses, which are the persisted state. | Stays in the default context. It is save-game state and must not reload. |
| `RimGovernor.Bridge.dll` | About 314 files. 55 files use Harmony and install patches through `new Harmony(id).Patch(...)` with callbacks as `typeof(X)` static methods, so the patches pin the Bridge. Statics include a `Timer` in `MainThreadWatchdog` that is never disposed. | Not a reload target. The Bridge is the game-facing adapter and is the stable side. |

So the existing assemblies split correctly already: Runtime and Bridge are the
stable host, and the brain is a new assembly beside them. The Go brain talks to
the Bridge through generated protobuf messages, and the port epic keeps that
protobuf stack "at the boundary". That boundary is the reload seam.

## Proposed shape

```
default context (never reloads)           collectible ALC (reloads)
  RimGovernor.Runtime   (state)             RimGovernor.Brain.dll
  RimGovernor.Bridge    (Harmony, tools)      policy, planners, scheduler
  Contracts (protobuf messages,              depends only on Contracts
    IBrain / IBrainHost interfaces)
  Google.Protobuf, Verse
```

- `Contracts` is a small assembly holding the generated protobuf C# messages and
  two interfaces: `IBrain` (start, tick or event input, stop/dispose, optional
  state export/import) and `IBrainHost` (the calls the brain makes back into the
  Bridge). It lives in the default context.
- A `BrainLoader` creates `new BrainLoadContext(isCollectible: true)`. Its
  `Load(AssemblyName)` returns null for `Contracts`, `Google.Protobuf`,
  `RimGovernor.*` and `Verse`/engine assemblies, so they bind to the default
  context and types are identical on both sides. Only `RimGovernor.Brain.dll` and
  brain-private dependencies load into the ALC. Load from a `byte[]` or a stream,
  not a path, so the DLL file is not locked while the game runs and a rebuild can
  overwrite it.
- Reload: stop the brain, call its dispose, drop every reference, `Unload()`,
  then force GC until a `WeakReference` to the context dies or a timeout passes
  (report failure to the log), then load the new build, construct `IBrain`, and
  hand back exported state.

## Unload blockers to design out

An ALC unloads only when nothing outside it references anything inside it.

1. **Harmony.** The brain should install no patches. If one is ever needed, give
   it a per-load Harmony id and `UnpatchAll(id)` on dispose. Today all patches
   are in the Bridge, which is fine.
2. **Registrations the brain makes in the host.** Capability providers, event
   subscriptions, `ToolRegistry` entries and anything stored in the statics
   listed above. Rule: the host holds only `IBrain`/`IBrainHost` and registers
   one stable forwarding provider, never types from the brain assembly. This is
   the point where the `CapabilityRegistry` gap above is avoided by design.
3. **Threads, timers and tasks.** `Task.Run` in the brain, `Timer`, thread-pool
   callbacks and `Channels` readers must be cancelled and awaited in dispose.
   The planned `CancellationToken` convention covers this. Test that disposal
   completes.
4. **Caches keyed by `Type`.** `System.Text.Json` option and metadata caches,
   `Activator`/reflection caches, `ConcurrentDictionary<Type,...>` in shared
   code, and Google.Protobuf descriptor registries if brain types are registered
   there. Keep brain types out of shared serializers or give the brain its own
   serializer instances. This is the usual cause of an unload that never
   completes.
5. **Statics and thread statics inside the brain.** Fine for unload (they die
   with the context) but they are state loss on reload. The port conventions
   (exceptions, plain records) already discourage ambient state.
6. **`AppDomain.AssemblyResolve` and `AssemblyLoadContext.Resolving` handlers**
   that return brain assemblies. Do not let the host resolver see them.

## State across a reload

- Durable state already lives in Runtime's `GameComponent`s and the persistence
  contracts ([persistence contract](../contracts/persistence-contracts.md)); a
  reload does not touch it.
- Brain in-memory state (due queue, claims, episode ledgers, session Method
  ownership) is lost unless exported. Choose one: rebuild from observed native
  frames (the architecture already treats observations as the source of truth and
  "unknown" as a value), or serialize with protobuf through `IBrain` so it
  crosses the context boundary as plain bytes. Rebuild-from-observation is
  cheaper and is the right first step.
- Reload only at a safe point: clock paused or between rounds, and no admitted
  action mid-dispatch. The native clock gives this.

## What does not reload

- `Runtime` and `Bridge` changes, including Harmony patches and Verse components.
  Those still need a game restart.
- Protobuf contract changes (`Contracts`) for the same reason: both sides must
  agree on the type identity.

## Complement: method-body hot reload

.NET's own Hot Reload (`MetadataUpdater.ApplyUpdate`, edit-and-continue deltas)
can apply method-body edits to already-loaded assemblies, including ones in the
default context such as Bridge. It needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in
the game's environment and a delta producer such as `dotnet watch`. Whether the
launcher can set the variable on the Waylith process, and whether the engine
leaves the needed debug-info and tiering behavior intact, is unverified. It
would help the Bridge inner loop; it cannot add types or change shapes the way
an ALC swap can.

## Costs and risks

- Debug builds of the brain run slower than the packaged build, and a collectible
  ALC adds some JIT and call overhead on type loads. Expected to be negligible
  for a once-per-round brain; measure it.
- A leaked reference makes `Unload` a no-op: the old brain keeps running next to
  the new one and memory grows per reload. The weak-reference check turns this
  into a logged error instead of a silent leak.
- A development feature only: production loads the brain once into the default
  context (or the same ALC without reload), so release behavior is unchanged.

## Spike results (out of game)

[contracts/tests/BrainReloadSpike](../../../contracts/tests/BrainReloadSpike/README.md)
loads a brain assembly from bytes into a collectible ALC, runs it, disposes it,
unloads it, and polls a `WeakReference` until the context is collected. Run on
.NET 10.0.12 (CoreCLR), two runs, identical. The "leak" and "retained-type" rows
are negative controls: they prove the harness can tell a blocked unload from a
clean one.

| Scenario | Result |
| --- | --- |
| Plain brain with statics and `[ThreadStatic]` | Unloads |
| Protobuf round trip with shared `Google.Protobuf` types (`Any`, JSON, binary, descriptors) | Unloads |
| `Task.Run` loop cancelled and awaited in Dispose; `Channel` reader completed in Dispose; `Timer` disposed with wait handle; dedicated thread stopped and joined; host event unsubscribed | Unload |
| Same, with the cleanup left out (task, timer, thread, event) | **Blocked** |
| Host keeps a brain `Type` (what an `AppDomain.GetAssemblies()` scan like `DefMirrorFill` could do) | **Blocked** |
| Reflection-based `System.Text.Json` on a brain type: default options, brain's own options, host-owned options, serialize only, plain POCO | **Blocked**, even after 8 s of GC polling |
| `System.Text.Json` through a source-generated `JsonSerializerContext` in the brain | Unloads |
| Brain loads its own copy of Contracts (no sharing policy) | Cast fails at the boundary, as expected |
| 25 load/run/unload cycles of the same assembly name | All 25 collected, no live contexts left |
| DLL on disk after loading from bytes | Writable (a rebuild can overwrite it). Loaded from a path, Windows locks it |

What this changes in the design:

- **Reflection-based JSON on brain types is out.** It pins the context regardless
  of whose options are used. Use `System.Text.Json` source generation inside the
  brain, or protobuf messages for anything that crosses the boundary. Any ported
  Go code that marshals structs to JSON needs this convention. See the root cause
  below.
- Hazard 4 above is therefore confirmed for JSON and not seen for protobuf with
  shared message types. Protobuf messages generated into the *brain* assembly
  (as opposed to shared Contracts) were not tested.
- The dispose rules (cancel and await tasks, dispose timers, join threads,
  unsubscribe) all work as written and are enough.
- Load from bytes, never from a path.

Still unverified and needs the in-game run: whether Waylith or the SDK pins
anything on its own (including `AppDomain.GetAssemblies()` scans), Harmony
unpatching of a brain-owned patch set, and behavior under the real tick load.

### Root cause of the JSON pin

Two independent static holders inside `System.Text.Json` (.NET 10.0.12) keep the
brain's `Type`s alive. Found by reflecting over the assembly's static state, then
clearing each piece separately before the unload:

1. **`DefaultJsonTypeInfoResolver.s_memberAccessor`**, a static
   `ReflectionEmitCachingMemberAccessor`. Its cache is a `ConcurrentDictionary`
   keyed by `(string, Type, MemberInfo)` holding the emitted constructor and
   accessor delegates. It has a 1 s sliding expiry and a 200 ms eviction
   interval, but eviction only runs on a later cache *miss*. It is transient:
   with unique options, host-side JSON work after the expiry frees it.
2. **The options caching context.** Each `JsonSerializerOptions` caches a
   `JsonTypeInfo` per `Type` in a `CachingContext`, and structurally equal
   options instances **share one context** (`TrackedCachingContexts`). A brain's
   `new JsonSerializerOptions()` equals the static `JsonSerializerOptions.Default`,
   so it reuses Default's context and its brain `Type` entries live as long as
   the process. This one is persistent. Options with a distinct setting
   (for example `MaxDepth = 37`) get their own context, which dies with them.

| Probe | Result |
| --- | --- |
| Reflection JSON, any options | Blocked |
| + clear the member-accessor cache only | Blocked (shared context still pins; unique options unload) |
| + clear the options caches (`Default`, host options) only | Blocked (accessor cache still pins) |
| + both, or the runtime's own `JsonSerializerOptionsUpdateHandler.ClearCache` | Unloads |
| Unique options, no clearing | Blocked |
| Unique options + clear accessor cache, or + host JSON activity after 2.5 s | Unloads |
| Default options + host JSON activity after 2.5 s | Blocked |

Consequences: a brain that must use reflection JSON can unload only with
brain-unique options plus later host JSON activity (or reflection into the
internal `ClearCache`, which is fragile across runtime versions). Source
generation avoids both holders and is the rule. Stored
`JsonSerializerOptions`, `Default`, and any `ConcurrentDictionary<Type,...>` in
shared code are the same class of pin and worth grepping for in host and
Bridge code the brain calls.

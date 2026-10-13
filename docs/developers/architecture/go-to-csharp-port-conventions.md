# Go-to-C# port conventions

Rules every port child of epic #3043 follows when it moves a Go package into the
`brain/` C# solution. Procedure and rationale for porting by hand rather than with
go2cs: [port spike](go-to-csharp-port-spike.md). This page fixes the layout, the
compiler settings and the Go-to-C# idiom map, then gives the per-package checklist
with `internal/domain` (`RimGovernor.Brain.Domain`, #3051) as the worked example.

Settled by the epic, not reopened here: the brain runs in-process; protobuf stays
at the boundary until the collapse child (#3100); Go stays in place until the
delete child (#3102); production code is ported and Go tests are not; behavior
preservation is best effort and bugs are fixed forward.

## Solution layout

```text
brain/
  RimGovernor.Brain.sln
  Directory.Build.props        net10.0, nullable, warnings as errors, locked restore
  Directory.Packages.props     central package versions (test packages only so far)
  Taskfile.yml                 build and test; wired into the root Taskfile
  src/RimGovernor.Brain.<Pkg>/ one project per ported Go package or cluster
  tests/RimGovernor.Brain.<Pkg>.Tests/
```

- One project per Go package (or tightly coupled cluster), named
  `RimGovernor.Brain.<PascalPackage>`: `internal/domain` is `RimGovernor.Brain.Domain`.
  Namespace equals assembly name. Project references follow Go import edges and
  must stay acyclic, as Go requires.
- File per Go file where that file is a unit (`haul.go` becomes `Actions/Haul.cs`);
  subfolders group families. A file holds the types Go declared in the file.
- Test projects are xUnit v3 and stay **empty** until a bug needs a regression test.
  Go tests are not ported (epic decision); the gate is the warnings-clean build,
  then the Go acceptance cases driven against the C# bot (cutover, #3085).
- Projects may reference the game assemblies at compile time through `RimWorldDir`
  (property in `Directory.Build.props`, same default as the native mod) and read
  immutable Def data directly; the domain project needs none.

## Compiler settings

`brain/Directory.Build.props` sets `Nullable=enable`, `TreatWarningsAsErrors=true`,
`WarningLevel=9999`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`,
`ImplicitUsings` and `RestorePackagesWithLockFile` (commit each `packages.lock.json`;
the Taskfile restores with `--locked-mode`). A port does not add `#pragma` or
`SuppressMessage` to get green; fix the code. A rule that is wrong for the whole
solution is turned off once, in `brain/.editorconfig`, with a one-line reason.

## Idiom map

| Go | C# |
| --- | --- |
| package `internal/x` | assembly and namespace `RimGovernor.Brain.X` |
| `type T struct{...}` with private fields, `NewT(...) (T, error)` | `sealed record T` with get-only properties and a validating public constructor that throws `DomainException`; no zero value |
| other `NewTFoo(...) (T, error)` constructors | `static` factory on `T` keeping the Go name (`ZoneCreate.NewFishingZone`) |
| `func (t T) Field() X` accessor | property `Field` |
| `func (a Action) Foo() (Foo, bool)` | property `Foo` of type `Foo?`, null when the kind does not match |
| `(T, error)` | return `T`, throw `DomainException` (invariant violation) or a typed subclass; no `Result<T>` |
| `errors.New("x")` returned from a validator | `DomainException.Require(cond, "x")` |
| sentinel `ErrFoo` and `errors.Is` | `DomainException` subclass (`DependencyException`, `WriteUnsentException`); callers `catch` it |
| `Validate() error` | `Validate()` that throws; add `bool IsValid` when Go callers test `Validate() != nil` |
| `err != nil` used as a predicate | provide a non-throwing `TryX` / `IsValid` form beside the throwing one |
| failed transition "preserves the original" | immutable value: the original is untouched when the new one throws |
| `context.Context` | `CancellationToken` as the last parameter; values carried on the context become explicit parameters (or `AsyncLocal`, see `ReadValidityScope`) |
| goroutine, `sync.WaitGroup` | `Task` / `Task.WhenAll`; no raw `Thread` outside the planner worker |
| `chan T` | `System.Threading.Channels.Channel<T>` (bounded where Go's was buffered) |
| `sync.Mutex`, `RWMutex` | `lock` on a private object; `SemaphoreSlim` when the section awaits |
| `select` | `Task.WhenAny` or `Channel` readers with the token |
| `defer` | `try`/`finally`, `using` |
| `panic` for programmer error | `InvalidOperationException`; never for domain failure |
| zero value is valid ("unknown") | `Fact<T>` default is unknown; other types have no zero value, use `T?` or a named `Unknown`/`None` member |
| `nil` pointer/slice/map | `null` only for "absent"; collections are never null (empty) |
| `Fact[T]`, `Known(v)`, `Unknown[T]()`, `v, ok := f.Value()` | `Fact<T>`, `Known(v)`, `Unknown<T>()`, `f.TryGetValue(out var v)`; Unknown stays a value, never an exception |
| defined `string` type with consts (`type Stage string`) | `enum Stage`; the Go string is the member's wire name (snake_case of the member unless `[WireName("x")]`), read with `Wire.Name` / `Wire.TryParse`; add `None = 0` with `[WireName("")]` when Go's empty string is a legal state; order members so `default` means what Go's zero meant |
| defined `string` type used as an id (`type PawnID string`) | `readonly record struct PawnId(string Value)` with ordinal equality, `CompareTo` and `<`; `default` is the empty id; `ID` becomes `Id` |
| defined integer type (`type Tick int64`) | `readonly record struct Tick(long Value)` with comparison operators |
| `validID(string(x))` | `x.IsValid` / `Identity.IsValidText(s)` |
| slice | `IReadOnlyList<T>` / `ImmutableArray<T>` in signatures; a slice field in a record Go compares with `==` is `EquatableArray<T>`; never copy defensively, values are immutable |
| `append([]T(nil), s...)` copy | not needed; `EquatableArray.From(s)` only to snapshot a mutable caller list |
| comparable struct holding JSON in a `string` so it stays comparable | the typed value (`EquatableArray<T>`); JSON is kept only where it is a real persisted text (a `Standard.Record`, an incident payload) and then uses source-generated `System.Text.Json` (`JsonSerializerContext`, never reflection; see [hot reload feasibility](brain-hot-reload-feasibility.md#root-cause-of-the-json-pin)) with the Go field names |
| `map[K]V` | `Dictionary<K,V>`; **never depend on iteration order** (Go randomises it): sort explicitly |
| `sort.Slice`, `slices.Sort` | `OrderBy(...).ThenBy(...)` (stable) with ordinal comparers |
| string `<`, `==`, `strings.Compare` | `StringComparison.Ordinal` / `string.CompareOrdinal`; ids already do this. Never culture compare |
| `len(s)` of a string (bytes) | `Encoding.UTF8.GetByteCount(s)` when a byte bound is enforced |
| `[]byte` | `byte[]` / `ReadOnlyMemory<byte>`; `json.RawMessage` becomes `string` of valid JSON text |
| `time.Time`, `time.Duration` | `DateTimeOffset` (UTC), `TimeSpan`; game time is `Tick`, never wall clock |
| embedded struct | composition (a property) plus explicit forwarding members; no inheritance to imitate embedding |
| interface | interface, same name with an `I` prefix |
| generics `func F[T any]` | C# generics; `constraints.Ordered` becomes `IComparable<T>` / `INumber<T>` |
| `init()` registration | explicit `Register...` call at startup (see `ActionKindExtensions.RegisterIntentKind`); no static constructor side effects |
| package-level `var` / `const` | `const` / `static readonly` on the owning type; with no owner, on `static partial class DomainConstants` (add a part per file) |
| package-level function `FooBar(x T)` | `static` method on the type it operates on (`ReviewStandard` becomes `Standard.Review`), or an extension method when `T` is an enum/struct; name kept otherwise |
| methods on a defined string type | extension methods on the enum |
| build tags | `#if` only for genuine platform splits; none exist in `domain` |
| struct comparison `a == b` | record value equality; sequence fields use `EquatableArray<T>` |
| JSON tags | dropped; serialization is a boundary concern until #3100. Whatever it becomes, brain assemblies must not use reflection-based `System.Text.Json` (it pins a collectible load context): source generation or protobuf only |

### Naming

- PascalCase members, `camelCase` private fields with a leading `_`. Two-letter
  initialisms follow .NET (`ID` to `Id`); longer ones keep their case (`HTTP` stays
  `Http` per .NET, `UUID` is not used).
- Keep Go names recognizable so a reader can grep across languages: `ActionKind`,
  `HeldReason`, `NewHaulAction`, `GenerationSnapshot`. Drop only a redundant
  family suffix on enum members (`HaulAction` becomes `ActionKind.Haul`).
- **BCL collisions get one fixed rename**, listed here so the next port does not
  rediscover it: `Action` becomes `DomainAction`. A member may not share its
  enclosing type's name (`Method.Method` becomes `Method.MethodId`).
- Public surface is the Go exported surface; unexported Go identifiers become
  `private` or `internal` (use `internal` when another file of the package reads it).

### Records, equality and immutability

- Every domain value is immutable. Types Go compared with `==` are records so equality
  and hashing are by value; add `EquatableArray<T>` for sequence members. Reference
  records (`sealed record`) for payloads and views; `readonly record struct` only for
  small ids and coordinates (`Cell`, `ActionDependency`).
- State transitions return a new value (`record with`); they throw
  `DomainException` on a violated precondition and leave the receiver unchanged.
- A constructor validates once. Do not re-validate an already constructed value the
  way Go re-checks zero-value-capable structs (`NewHaulAction` does not re-run
  `NewHaul`).
- `sealed` everything; no public setters; `init` only on view records Go mutated by
  copy (`ProgressView`).

### Closed variants

Go's tagged struct (`Action` with a field per family) becomes a kind plus one payload
record per family. `DomainAction` is built only by the per-family
`New<Family>Action(id, payload)` factories, so it is always canonical and the Go
re-canonicalisation switch in `NewPlan` disappears. Each family file declares its
payload record and a `partial record DomainAction` part with its factory and its
typed accessor, exactly like `Actions/Haul.cs`.

### Async, cancellation and threading

The domain package has no I/O. Packages that do: every method that waits takes a
`CancellationToken` and passes it to everything it awaits; it throws
`OperationCanceledException` and never swallows it. Goroutines that fan out become
`Task`s joined with `Task.WhenAll`; ordered results are reduced in the Go-defined
order, not completion order. Producer/consumer goroutines become a `Channel<T>` plus a
reader `Task`. The planner worker thread never calls game APIs that touch world state;
game data comes through `IStatSource` (the Go `stateval` is replaced, not ported).

### Unknown and Unclear

Explicit unknowns stay values: `Fact<T>`, `Finding.Unclear`, `Effect.Unknown`,
`Receipt.Unknown`, `RefusalClass.Unknown`. Do not turn them into `null`, a default or an
exception, and do not collapse an unknown into a known value.

## Per-package port checklist

A port child works through this in order. `internal/domain` is the worked example
(about 7.1k production lines, 90 files).

1. **Read the slice.** List the Go package's exported surface, its generics, its
   goroutine launches, its `(T, error)` returns, its package-level state and its
   `init` registrations. *Domain:* no goroutines; one `init` registry
   (`RegisterIntentKind`); one generic type (`Fact[T]`); package vars `ErrDependency`,
   `ErrWriteUnsent`; closed variant `Action` with 77 kinds.
2. **Create the project.** `src/RimGovernor.Brain.<Pkg>`, add it to the solution and
   the Taskfile, reference only already-ported packages. *Domain:* no references.
3. **Write the foundation first.** Ids, `Fact<T>`, exceptions, enum conventions
   (`Wire`), `EquatableArray<T>`, the shared enums used everywhere (`ActionKind`).
   *Domain:* `Foundation/`.
4. **Port leaf files, then dependents.** Value types, then constructors and
   validation, then the aggregate that composes them. *Domain:* payload records
   (`Actions/`), then `DomainAction`, `PlanSpec`, `Progress`, `Standard`.
5. **Apply the idiom map mechanically.** No redesign; fix bugs forward and note
   them. Keep the Go file order so a diff against the Go source stays readable.
6. **Handle ordering and equality explicitly.** Replace every map iteration, sort and
   string compare using the rules above. *Domain:* dependency sorting, cycle detection
   and the plan's dependency checks.
7. **Build warnings-clean.** `dotnet build brain/RimGovernor.Brain.sln` with no
   suppressions, then `task build`.
8. **Leave Go untouched** and do not port its tests; list in the commit body anything
   knowingly dropped or changed (for example re-validation removed, or a bug fixed).
9. **Record what is unverified.** The commit states that behavior parity is checked
   only by the cutover acceptance run (#3085).

## Verification

`task build` runs `brain:build` (restore in locked mode, `dotnet build` with warnings
as errors). `task test` runs `brain:test`; it is empty until a bug needs a test.

`RimGovernor.Brain.Policy` (Domain + Contracts) hosts the policy ports.

## Wire records (`RimGovernor.Brain.Contracts`, #3100)

The wire contracts are plain C# records in `RimGovernor.Brain.Contracts` (namespace
`RimGovernor.Contracts.Wire.<Package>`, one file per former `.proto`), referenced by the brain and the native
mod. They were generated once from `contracts/proto` and are now owned by hand; the schemas in
`contracts/proto` are no longer the source of truth for C#. Shape rules:

- `sealed record` per message with `init` properties; a message-typed field is `T?`, a field with presence
  (`optional`) is `T?`, a field without presence is `T` with its zero value, a string defaults to `""`.
- A list is `EquatableArray<T>` (collection expressions build it); a map is `IReadOnlyDictionary<K,V>`; bytes are `byte[]`.
- An enum keeps the proto member names (prefix stripped, `Unspecified = 0`) and `[WireName("PROTO_NAME")]`; JSON
  uses that name as a string.
- A `oneof` is an abstract record `<Message><Oneof>` with one `<Case>Case(T Value)` record per arm, discriminated by
  `type` through `[JsonPolymorphic]`, and an implicit conversion from each message-typed payload. The message holds
  it in a nullable property named after the oneof (`reply.Outcome is ...Outcome.FailureCase { Value: var failure }`).
- JSON is source-generated `System.Text.Json` (`WireJsonContext`, entry point `WireJson`): camelCase property names
  (the names ProtoJSON emits), enums as strings, 64-bit integers as numbers, defaults omitted, unknown members
  refused, `type` accepted anywhere in an object. Changing the casing is the single
  `PropertyNamingPolicy` in `WireJsonContext`.
- The def mirror (`defs.proto`) is not a wire contract here: only the six game enums the observations carry
  (`Defs.cs`) exist; the catalog replies drop `defs`, `thing_defs`, `terrain_defs` and `game_constants` (the brain
  reads Def data directly). Four type names changed to satisfy analyzers: `Clock.Event` is `ClockEvent`,
  `Mirror.ThingFlag` is `ThingFlagBit`, `Observations.BillStack` is `BillStackRow`; six properties named like
  their message got a different name (`Letters.Items`, `Messages.Items`, `Alerts.Items`, `Amount.Value`,
  `Thing.Entity`, `Trader.Entity`) and keep their JSON name.

## Stat reads and the game assemblies (A1b, #3056)

`IStatSource` (`RimGovernor.Brain.Domain`, `Stats/`) is the brain's only way to read computed
stat values. It replaces Go `internal/bridge/stateval`, which is not ported; `FakeStatSource`
serves tests and simulation with recorded values, and the production implementation is the
game-thread stat service (#3080; [native facade](native-facade.md)).

**Bare-host answer (checked 2026-10-12): yes.** The game ships on .NET 10 with managed
`Assembly-CSharp.dll` (the isolated copy under `.rimgovernor/native-rimworld/`; the Steam default
path of `RimWorldDir` does not exist on every machine). A plain `net10.0` console project that
references only `Assembly-CSharp.dll` (the other game DLLs are copied beside it) runs, with no
engine started:

- `new ThingDef { ... }`, `new StatDef { ... }`, `StatModifier` and `StuffProperties` construct.
- `statDef.Worker.GetValueAbstract(thingDef, stuff)` returns the real `StatWorker` result: the
  `statBases` value (42) and the stuff factor (84 with a factor-2 stuff); `ShouldShowFor` runs.
- Anything that reads `Find.*` statics sees null, not a crash at load: `Find.Scenario` is null,
  and `ThingDef.CostListAdjusted` threw `NullReferenceException` from `CostListCalculator` on a
  hand-built def with no cost list or game. Parts that read scenario, difficulty, mods, maps or
  pawns therefore need real game state, which is exactly what `StatContext` facts and the
  game-thread service (#3080) supply.

So derivations can read real `Def` objects built from the game's own types without a running
engine, but stat values that depend on world state must still come through `IStatSource`.

## The catalog bridge (`RimGovernor.Brain.Bridge`, #3056)

`DefinitionCatalog` (Go `internal/bridge/catalog*.go`) is a `sealed partial class` over the game's own
Def objects, one file per Go file under `Catalog/`. It references Domain and Policy and returns Policy
types; it never mirrors a Def. Conventions that differ from the Go code:

- Defs are read through `Assembly-CSharp` (`Rows<T>()`, `DefRow<T>`, `ThingRow`), not through a wire row
  type. The `Wire/Defs.cs` mirror is not the Def surface, and the mirror decode, fingerprint reuse and
  `catalog_disk.go` (a disk cache of wire data) have no counterpart. Products are `Lazy<T>` fields.
- Stat values come from `IStatSource`, never from the Def. Private game members are read through the typed
  `UnsafeAccessor` declarations in `GameMembers`, and private constants through `GameConstants`, so a rename
  in the game fails at the first call rather than returning nothing.
- Where Go distinguished "unset" from zero on the wire, the answer is the game's own value (an unset trader
  `countRange` is the zero range and answers "no").
- An unreadable or inconsistent def throws `CatalogException`; unknown-but-legal answers are `Fact<T>`.

**Unavailable builds.** The project compiles against `Assembly-CSharp.dll`, `UnityStubs.dll` and
`Waylith.dll` from `RimWorldDir`. Where that directory has no `Assembly-CSharp.dll` the project builds
as an empty assembly (only `Properties/Unavailable.cs`) and prints
`RimGovernor.Brain.Bridge: unavailable (missing ...)` from the `ReportBridgeUnavailable` target, so
`task build` still succeeds on a machine without the game. Nothing may reference the Bridge types
without the same guard.
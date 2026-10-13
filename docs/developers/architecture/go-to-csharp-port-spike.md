# Go-to-C# port: go2cs spike and fallback (#3048)

Spike for epic #3043. Question: is a deterministic transpiler (go2cs) worth
using for the port, or is agent-driven per-package porting cheaper?

**Verdict: no-go on go2cs. Port by agent, package by package, with the
procedure at the end.**

## Kill criteria (stated before the run)

1. Abandon go2cs if fewer than 80% of files compile unaided.
2. Abandon it if cleaned output is not clearly cheaper than a direct agent
   port, where "cheaper" includes reaching the epic's conventions (exceptions,
   `CancellationToken`, Tasks and `Channels`, plain records, xUnit v3), not just
   a green compile.

## Tool

- go2cs `226afb810a21408ebda2412dd74d526345c46f65` (HEAD of
  `GridProtectionAlliance/go2cs`, 2026-10-12), release `1.24.13.6`, built with
  Go 1.27.0, net10 SDK 10.0.401. Cloned into an empty scratch directory and
  run only on copies. The repo is not stale: it is under daily commit activity.
- The pinned stdlib corpus is Go 1.24.13. `-tests` refuses a Go 1.27.1 tree
  until `GoStdLibVersion` is edited in the clone (done for the spike). Converted
  stdlib code was therefore 1.24-shaped against 1.27 input.

## Measurements

| Input | Go lines (non-blank) | Files | Converted | Compiled unaided | After cleanup |
|---|---|---|---|---|---|
| `internal/domain` prod | 7,064 | 90 | 90/90 (4 s) | 86/90 (95.6%) | 90/90 |
| `internal/domain` tests | 4,451 | 69 | 69/69 | 64/69 (92.8%) | 69/69, 190/190 tests agree with `go test` |
| `internal/combatdispatch` (generics + goroutines) | 97 + tests | 2 | 2/2 | 2/2 | 4 tests agree, but see flake below |
| `internal/policy` quest slice | 3,878 prod, 28 files | 28 | converted with the package (568 files, 33 s) | not measured | not measured |

- Unaided compile failures in domain prod: `incident.go` (Go 1.27
  `json.RawMessage` is `jsontext.Value`, absent from the 1.24 corpus), and
  `<` / `slices.Sort` on defined string types (`PawnID`, `ActionID`) in three
  files. Test files: `== "x"` against an indexed defined-string element emitted
  a C# `string` instead of `"x"u8` in 5 files (4 lines).
- Hand cleanup to compile: 9 lines (1 Go-side, 4 production, 4 test) over
  15,186 generated lines: about 0.6 per 1k generated. The compiler had masked
  later-stage errors in the first pass, so the count only appeared after reruns.
- Policy slice compile rate is not measured. The policy package needs
  `defspb` (the 62k-line def mirror) and protobuf-go. Plain `-recurse -tests`
  spent 28 minutes at 100% CPU on `defspb` and was killed. `-recurse=module`
  excludes `-tests` and left protobuf-go unconverted, and the 568-file policy
  project build had not finished after 10 minutes and was killed. The quest
  files' 12 of 28 production files and 16 of 26 test files reference protobuf
  types directly, so the slice cannot compile without hand-binding to C#
  protobuf in any case.

## Idiom

go2cs emits Go emulation, not C#. Sample from `combatdispatch.Dispatch`:

```csharp
ref var report = ref heap<Report<T>>(out var Ꮡreport);
var next = new channel<nint>(0);
ref var wg = ref heap(new sync.WaitGroup(), out var Ꮡwg);
goǃ(() => {
    GoFrame ᒐ = default;
    try { defer(Ꮡwg.Done, ref ᒐ); foreach (var i in nextʗ1) { ... } }
    catch (Exception ᒐex) when (GoFrame.IsPanic(ᒐex, out PanicException? ᒐp)) { GoFrame.Capture(ᒐp); }
    finally { ᒐ.Run(); }
});
next.ᐸꟷ(i);
```

and from `domain`: `(T, error)` tuples with `default!` for nil, `@string`
and `slice<T>` / `map<K,V>` runtime types, `ΔAction`-style renamed types,
`partial struct` for every Go struct, `UntypedInt` constants, and source-generated
`.g.cs` partials per defined type. Every file depends on `golib`, a Roslyn
generator and a converted stdlib (`fmt`, `bytes`, `encoding/json`, ...).

- Generics: emitted as generic methods and structs and compiled (identity.go's
  `Fact[T]`, `slices.Sort[S,E]` call sites). Defined-string element types fail
  the numeric-operator constraints (`CS0315`).
- Goroutines and channels: emulated (`goǃ`, `channel<T>`, `defer` frames), not
  Tasks or `System.Threading.Channels`. `TestDispatchReducesInSeedOrderNotCompletionOrder`
  passed under Go and failed under the C# build in 1 of 4 comparison runs
  (scheduling-dependent ordering).

## Why no-go

- Criterion 1 passes (about 93-96% on domain), but only on a leaf package.
  The policy slice, the actual target, does not compile or finish converting
  without the protobuf stack, which the epic keeps at the boundary.
- Criterion 2 fails. The output contradicts the settled decisions: errors are
  tuples, not exceptions; goroutines are emulation, not Tasks; structs are
  partial value types with generated companions, not records. Reaching the
  conventions means rewriting most lines anyway, after paying for the tool's
  runtime, generator and a 1.24-vs-1.27 stdlib mismatch that the brain would
  carry inside the game process permanently. Test porting is the parity gate;
  unreviewable emulation idioms in tests defeat that.
- Low cleanup count per 1k lines is real but measures only "compiles".

## Fallback: agent-driven per-package port

Applies to every port child (#3051 onward). A2 (#3049) fixes the conventions;
this is the procedure.

1. One child, one package slice, tests ported with the code. Go stays.
2. Read the Go slice and its tests; list its exported surface, generics,
   goroutine launches and `(T, error)` returns.
3. Port in dependency order, leaf files first. Map: `(T, error)` to a return
   value with exceptions at failure points; `context.Context` to
   `CancellationToken`; goroutines/channels to Tasks and
   `System.Threading.Channels`; Unknown/Unclear domain values stay explicit
   values; defined-string IDs to `readonly record struct` with an ordinal
   comparer (the ordering Go gets from `<`); generics to C# generics.
4. Port each `_test.go` beside its file as xUnit v3. The test set must keep its
   test count; a Go test that cannot port is listed in the commit body.
5. Build and run the new tests; do not delete the Go.
6. Treat sorting and map iteration explicitly: Go map order is random, so
   ported code must not depend on it; Go string compare is ordinal bytes, use
   `StringComparison.Ordinal`.
7. Scheduling-dependent tests (see the flake above) are re-expressed with
   events, not sleeps.

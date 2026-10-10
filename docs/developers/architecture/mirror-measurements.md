# Def mirror measurement baseline

What the def mirror costs, before and after the game constants were added to it
(epic #2621: #2626, #2627, #2628). The #1720 acceptance asked for these numbers.
Mechanism: [schema generation](../../../contracts/schema-generation.md#game-constants).

## Refresh

Game-free numbers (recording, generated files, counts, Go decode):

```
go run ./cmd/mirrorstats          # from go/; -runs <n> for more decode repetitions
```

`Defs.cs` is gitignored; the tool prints `absent` until
`go run ./internal/protobufgen/cmd/generatecsharp` has produced it. Native fill
and build times need the game and are measured by hand: read the full catalog
twice from a headless game with all DLCs (the first read runs the class
initializers) and compare the `DefMirrorFill.BuildStatics` time to the whole
read; build with `acceptance setup -rebuild`. Refresh the recording first with
`recordcatalog` ([recording the catalog](../testing/recording-the-catalog.md)), then update the table.

## Numbers

Measured 2026-10-10, game 1.6.4871 with all five DLCs, recording
`go/internal/observation/testdata/full_catalog.pb.gz`.

| Quantity | Before constants | After (main at 43e48208a) |
|---|---|---|
| Recording, raw | 11,920,508 B | 11,979,644 B |
| Recording, gzip | 1,914,000 B | 1,947,460 B |
| `game_constants` on the wire | none (16 `CatalogConstants` fields) | 59,132 B |
| `defs.proto` | 1.49 MB | 2,560,153 B |
| `defs.pb.go` | 14.96 MB | 22,482,351 B |
| `Defs.cs` (generated, gitignored) | about 57 MB | 85,473,266 B |
| Messages in `defs.proto` | 2,838 | 4,770 |
| Fields in `defs.proto` | 24,212 | 32,613 |
| Enums in `defs.proto` | 133 | 378 |
| Constants messages (`*Constants` plus root `GameConstants`) | 0 | 1,932 |
| Constants fields (members plus the root's one field per class) | 0 | 8,401 (6,470 members in 1,931 classes) |
| Go decode of the whole recording | 55 ms | 59 ms |
| Native fill of the statics | n/a | 459 ms first read, 86 ms warm |
| Native full-catalog read | n/a | 3.2 s first, 1.7 s second |

How the columns were obtained:

- "Before" recording and Go decode: the same recording with `game_constants`
  cleared and re-encoded by `mirrorstats`. "Before" file sizes, enum count and
  the `Defs.cs` figure are from the #2626 landing comment; the message and field
  counts are the after counts minus the constants messages and fields.
- Go decode is the median of 9 `proto.Unmarshal` runs on a shared, loaded
  machine. Repeated runs vary by tens of milliseconds (55 to 95 ms for the same
  input) and the with/without difference (1 to 4 ms) is inside that noise. The
  constants decode alone was measured at about 1 ms in #2628.
- Native timings are from the #2628 landing comment (headless, all DLCs). The
  statics are about 14 percent of a cold read and 5 percent of a warm one. The
  creation reply grew about 338 KB of JSON (8.34 to 8.68 MB). Native build:
  `setup -rebuild` takes about 1 minute; no before-value was taken, so the
  build-time cost of the larger `Defs.cs` is not separated out.
- Two recordings of the same game carry identical `game_constants`; their def
  rows differ in 43 places that are runtime caches (#2628).

## Reading the result

The constants cost 59 KB on the wire and under 1 percent of the recording, 5 to
14 percent of the native catalog read, and a doubling of generated C#
(`Defs.cs` +28 MB, `defs.pb.go` +7.5 MB), which is the cost that scales with the
number of classes covered. Widening the generator's `ConstantNamespaces` should
be weighed against that growth, not the wire size.

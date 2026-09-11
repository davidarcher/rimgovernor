# Official Go Protobuf leg

Canonical schemas live in `contracts/proto`. The official `protoc-gen-go` plugin
and Go runtime are pinned to **v1.36.11**. The wrapper checks **libprotoc 30.0**, the
compiler bundled in Grpc.Tools **2.72.0**. Supply its Windows or Linux executable
explicitly; use the repository Go version from `go/.go-version` (currently 1.27.1).

From the repository root:

```text
python scripts/generate_protobuf_go.py --protoc <official-protoc> --output .rimgovernor/go-protobuf-generate-01
python scripts/generate_protobuf_go.py --protoc <official-protoc> --output .rimgovernor/go-protobuf-check-01 --check
```

`--go` selects an explicit Go executable. `--proto-root` selects a coordinated
schema snapshot; it defaults to the canonical directory. Every artifact directory
must be fresh. The wrapper installs the pinned official plugin into that directory,
records input/compiler hashes and command outputs in `result.json`, compiles and
vets the generated module, and verifies downloaded module checksums. It does not
interpret schemas or emit Go source. Generation owns only `*.pb.go` beneath the
wire module; module metadata remains explicitly maintained. `--check` compares the
complete owned file set without changing it, allowing checkout line-ending differences.
Network access to Go module distribution is required for a fresh private cache.

The wire module is `github.com/davidarcher/RimGovernor/go/internal/wire`, located at
`contracts/generated/protobuf/go`. This matches existing schema `go_package`
imports through the official `module` generator option. This proof module uses a
local `replace` to that directory. Production `go/go.mod` is deliberately unchanged;
its later integration must explicitly require/replace the wire module. Do not copy
or edit generated types into a competing package. Run module checks in both modules;
the production module's `go test ./...` does not traverse these separate modules.

From `tools/protobuf/go`:

```text
go test -mod=readonly ./...
go vet -mod=readonly ./...
go mod tidy -diff
go mod verify
go run -mod=readonly . --output ../../../.rimgovernor/go-protobuf-fixtures-01
```

The last command emits original Go request/reply/UInt64Value/context JSON and binary
fixtures. Pass their directory to the C# proof's `--cross-language-inputs` option.
Then validate the C# origin fixtures and its separate Go echoes:

```text
go run -mod=readonly . --output ../../../.rimgovernor/go-protobuf-cross-01 --cross-language-inputs <csharp-roundtrip-dir> --check-go-echo
```

C# origin fixtures include placement, UInt64Value, ObservationContext and authority
active/inactive/acquire messages. They are checked for binary/ProtoJSON agreement
and emitted as `csharp-echo-*`; the input originals are never overwritten. Go echoes
must equal the originating typed Go fixtures. JSON whitespace and byte order are
not used as a canonical identity. The proof includes present zero/false, missing
stock, int64/uint64 extremes, oneof conflicts, malformed input, and observed binary
unknown-field retention. Ordinary validators still enforce required fields, supported
enums, bounded collections/IDs and game invariants. This foundation proof is not
full-family, native adapter, Mono runtime or gameplay acceptance.

Official sources: [Go release](https://github.com/protocolbuffers/protobuf-go/releases/tag/v1.36.11),
[Go generation](https://protobuf.dev/reference/go/go-generated/),
[ProtoJSON](https://protobuf.dev/programming-guides/json/).

## Full-package serialization shapes

The proof emits `manifest.tsv` with `id`, `message`, `json`, `binary` tab-separated
columns and a separate `coverage.json`. Generated test fixtures exercise every
registered canonical message, each real oneof arm, enum values, and absent versus
present defaults; message nesting is bounded to three levels. Go cases use stable
sorted `go-shape-*` IDs. These shapes test serialization only and intentionally
include values that ordinary domain validators must reject.

Cross-language mode requires the C# `manifest.tsv`. It resolves fully qualified
message names through the official generated registry, compares JSON and binary,
and emits `csharp-echo-*` fixtures plus `csharp-echo-manifest.tsv`. All canonical
packages must be imported into this proof; add new package imports when the shared
package grows. Missing/unregistered messages fail instead of being discarded.
The fixed handcrafted fixtures continue to exercise meaningful presence and
correlation values separately from descriptor coverage.

`--check-go-echo` also compares every `go-echo-go-shape-*` row in the incoming
manifest with its freshly emitted `go-shape-*` original, including the exact
message type and typed value. Empty, missing, duplicate, extra and changed sets
fail. C# origin rows are counted separately from Go echoes and must cover every
registered canonical message type. Echo filenames and IDs both receive the
`csharp-echo-` prefix; this exact prefix is removed by the reciprocal comparison.
The output main manifest includes both original Go rows and C# echo rows.

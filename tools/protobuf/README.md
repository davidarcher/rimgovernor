# Official Protobuf toolchain

`contracts/proto/*.proto` owns the message schema. Generate C# with the official
compiler; use official `Google.Protobuf` parsing and formatting for ProtoJSON and
binary messages. Ordinary boundary code owns domain presence, enum, count and
length checks. No custom schema compiler or validation language is involved.

From the repository root:

```powershell
go -C go run ./internal/protobufgen/cmd/generatecsharp --dotnet <dotnet>
go -C go run ./internal/protobufgen/cmd/generatecsharp --dotnet <dotnet> --check --proof --output .rimgovernor/protobuf-proof-01
```

The generator is a stdlib-only Go program under the repository Go module, so
gofmt, vet, staticcheck and `go test` cover it. It locates the repository root
by walking up from the working directory (`--root` overrides) and resolves
relative paths there. It restores the committed lock in a fresh private tree, verifies the
compiler version, compiles every canonical `.proto`, and emits C# plus a descriptor
set containing imports. `--check` compares generated text while allowing checkout
line-ending differences. It does not change checked-in outputs. The compiler
process has a bounded timeout; failed restore/build artifacts remain in the output.

`--proof` compiles all generated messages as .NET Framework 4.7.2 and executes the
placement binary/ProtoJSON tests. Windows requires .NET Framework; Linux requires
Mono to execute that target. The proof does not require game files. Its artifacts
include `csharp-request`, `csharp-reply`, and `csharp-u64`, each as `.json` and `.bin`.
Pass `--cross-language-inputs <directory>` with `--proof` to read the corresponding
`go-request`, `go-reply` and `go-u64` pairs and re-emit their typed contents. This is
wire and framework acceptance, not native SDK or gameplay acceptance.

## Pinned inputs

The project and `packages.lock.json` pin these official dependencies:

- [Grpc.Tools 2.72.0](https://www.nuget.org/packages/Grpc.Tools/2.72.0), bundling
  `libprotoc 30.0`. The script runs `protoc` directly; no gRPC service runtime is
  generated or required. Service declarations remain in descriptor output.
- [Google.Protobuf 3.31.1](https://www.nuget.org/packages/Google.Protobuf/3.31.1),
  using its .NET Framework 4.5 asset for the .NET Framework 4.7.2 target.
- Microsoft.NETFramework.ReferenceAssemblies 1.0.3 for compilation.

The development proof uses .NET SDK 8.0.424. The locked .NET Framework runtime
closure is Google.Protobuf 3.31.1, System.Memory 4.5.3, System.Buffers 4.4.0,
System.Numerics.Vectors 4.4.0 and System.Runtime.CompilerServices.Unsafe 4.5.2.
The native package must stage the runtime closure and required notices when the
adapter is integrated. Compiler and reference-assembly packages are build inputs,
not game runtime dependencies. The lock records package content hashes; do not
copy unversioned DLLs from another mod.

Google.Protobuf declares BSD-3-Clause in its NuGet manifest, with upstream source
revision `74211c0dfc2777318ab53c2cd2c317a2ef9012de`.
Preserve the [upstream license](https://github.com/protocolbuffers/protobuf/blob/v31.1/LICENSE)
and each redistributed dependency's license/notice material in native packaging.

## Wire behavior

Use [ProtoJSON](https://protobuf.dev/programming-guides/json/) rather than generic
JSON serialization of generated CLR properties. Its enum names, oneof fields and
64-bit decimal strings are the wire representation. Optional scalar fields retain
presence: known zero/false differs from missing; absent `available` means unknown
stock. Unknown numeric proto3 enum values still require a domain check. ProtoJSON
parser behavior replaces the experimental hand-written JSON contract's grammar
rules; no historical JSON parity gate applies.

See the official [C# generated-code guide](https://protobuf.dev/reference/csharp/csharp-generated/)
for generated presence and oneof APIs. Generated C# files are committed under
`contracts/generated/protobuf/csharp`; edit the `.proto` inputs and rerun the
compiler instead of editing generated code.

## Complete package proof

The proof discovers every compiled canonical message and emits serialization
shapes covering presence, enums and oneof variants. `manifest.tsv` carries each
shape inline as one tab-separated row: fixture ID, fully qualified message,
ProtoJSON and the base64 binary encoding (header `id\tmessage\tprotojson\tbinary-base64`),
so thousands of shapes cost one file rather than one `.json`/`.bin` pair each.
Only the handcrafted named fixtures are written as files. These shapes test the
official runtimes; they are not all valid native requests.

Run the Go proof to create independent origins, pass that output through
`--cross-language-inputs`, then run Go again with the resulting C# directory and
`--check-go-echo`. Finally invoke the generated `ProtobufProof.exe` with
`--verify-return <csharp-origin-directory> <go-return-directory>`. This verifies
the complete original set, message types and values including field presence,
not only agreement between each returned JSON/binary pair. Linux uses Mono with
the .NET Framework/netstandard facades (Debian `mono-devel`). `task protobuf:build`
and `task protobuf:test` (this directory's `Taskfile.yml`) run the drift checks and
the exchange in that order; CI runs them on both platforms and preserves the
exchange artifacts.

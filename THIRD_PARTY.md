# Source and dependency notices

## Go dependencies

Pinned versions and hashes are in `go/go.mod` and `go/go.sum`. Retained notices
in `third_party/` cover modernc SQLite.
The OpenTelemetry Go SDK and OTLP exporters (`go.opentelemetry.io/otel/...`,
with gRPC, protobuf, go-logr, backoff, xxhash and grpc-gateway beneath them) are
Apache-2.0, BSD-3-Clause or MIT; their module LICENSE files are the notices to
ship. They enter through `go/internal/otel` (see the
[OTEL core contract](docs/developers/contracts/otel-core.md)).
Include applicable dependency notices with distributed binaries.

## Native integration and formatters

The official Protobuf toolchain uses Google.Protobuf3.31.1 and Grpc.Tools2.72.0
(protoc30.0), with the runtime closure pinned in
`tools/protobuf/packages.lock.json`. The generated Go wire module pins
google.golang.org/protobuf1.36.11 in its own go.mod/go.sum. Protobuf runtimes use
BSD-3-Clause; the Grpc.Tools build package declares Apache-2.0. Runtime distribution must include their upstream notices
and notices for the .NET runtime dependencies; compiler/reference packages are
build inputs. See [toolchain provenance](tools/protobuf/README.md) and
[Go generator provenance](tools/protobuf/go/README.md). The native mod no longer ships a protobuf runtime (the wire is plain C# records with System.Text.Json, #3100); the build manifest records any bundled NuGet runtime dependencies and file hashes, and actual native loading remains part of N01 package acceptance.

- [Colony bridge source notice](integrations/rimgovernor-native/Notices/companion/PROVENANCE.md)
- [GABP host (vendored RimBridgeServer and Lib.GAB, MIT) notices and commit SHAs](integrations/rimgovernor-native/Notices/host/PROVENANCE.md)
- [Headless adapter source notice](integrations/rimgovernor-native/Notices/headless/PROVENANCE.md) and GPL-3.0 license

Game files and artwork are supplied separately. The RimWorld 1.7 install (Waylith engine, its CoreCLR/net10 runtime, UnityStubs and Harmony 3 from the Workshop) is the game and Harmony's own and is neither bundled nor redistributed by the mod; the mod compiles against it. Encrypted remote-acceptance bundles of the install are described in [remote bundles](docs/developers/remote-bundles.md).

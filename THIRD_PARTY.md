# Source and dependency notices

## Go dependencies

Pinned versions and hashes are in `go/go.mod` and `go/go.sum`. Retained notices
in `third_party/` cover the MCP Go SDK, modernc SQLite, SQLite and sqlite-vec.
Include applicable dependency notices with distributed binaries; the Go packaging
gate remains in [G01.11](docs/BACKLOG.md).

## Dashboard

The dashboard includes source derived from IlyaChichkov/rimapi-dashboard at
`152454bbcc8ab7d2b3e6cfff797f6a1df03d36b1`. Its MIT notice is retained in
[third_party/rimapi-dashboard-LICENSE](third_party/rimapi-dashboard-LICENSE).

## Native integration and formatters

The official Protobuf toolchain uses Google.Protobuf3.31.1 and Grpc.Tools2.72.0
(protoc30.0), with the runtime closure pinned in
`tools/protobuf/packages.lock.json`. The generated Go wire module pins
google.golang.org/protobuf1.36.11 in its own go.mod/go.sum. Protobuf runtimes use
BSD-3-Clause; the Grpc.Tools build package declares Apache-2.0. Runtime distribution must include their upstream notices
and notices for the .NET runtime dependencies; compiler/reference packages are
build inputs. See [toolchain provenance](tools/protobuf/README.md) and
[Go generator provenance](tools/protobuf/go/README.md). Native runtime staging
and distribution notice checks remain in N01 adapter/package acceptance.

- [Colony bridge source notice](integrations/rimgovernor-native/Notices/companion/PROVENANCE.md)
- [UI formatter source notice](controller/rimgovernor/vendor/PROVENANCE.md)
- [Headless adapter source notice](integrations/rimgovernor-native/Notices/headless/PROVENANCE.md) and GPL-3.0 license

Game files, artwork, GABS and installed SDK assemblies are supplied separately.

The offline `contracts/tests/parser/PlacementParserProbe.cs` retains native
validation under the colony bridge source notice. Standalone contract tests
reference Newtonsoft.Json 13.0.4 (the net45 assembly matches the native parser
baseline) and Microsoft.NETFramework.ReferenceAssemblies.net472 1.0.3 from NuGet.
These test references do not bundle game assemblies.

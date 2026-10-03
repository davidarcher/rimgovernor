# Source and dependency notices

## Go dependencies

Pinned versions and hashes are in `go/go.mod` and `go/go.sum`. Retained notices
in `third_party/` cover modernc SQLite.
Include applicable dependency notices with distributed binaries.

## Native integration and formatters

The official Protobuf toolchain uses Google.Protobuf3.31.1 and Grpc.Tools2.72.0
(protoc30.0), with the runtime closure pinned in
`tools/protobuf/packages.lock.json`. The generated Go wire module pins
google.golang.org/protobuf1.36.11 in its own go.mod/go.sum. Protobuf runtimes use
BSD-3-Clause; the Grpc.Tools build package declares Apache-2.0. Runtime distribution must include their upstream notices
and notices for the .NET runtime dependencies; compiler/reference packages are
build inputs. See [toolchain provenance](tools/protobuf/README.md) and
[Go generator provenance](tools/protobuf/go/README.md). Native runtime staging uses the Bridge project's own lock and retains
[the runtime closure notices](integrations/rimgovernor-native/Notices/protobuf/PROVENANCE.md).
The build manifest records bundled runtime dependency versions and file hashes;
actual native loading remains part of N01 package acceptance.

- [Colony bridge source notice](integrations/rimgovernor-native/Notices/companion/PROVENANCE.md)
- [Headless adapter source notice](integrations/rimgovernor-native/Notices/headless/PROVENANCE.md) and GPL-3.0 license

Game files, artwork and installed SDK assemblies are supplied separately.

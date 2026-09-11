# Native Protobuf runtime notices

The Bridge project uses Google.Protobuf 3.31.1. Its `packages.lock.json` records
NuGet content hashes and the complete resolved dependency graph. The package
builder restores in locked mode and stages the actual NuGet runtime DLLs emitted
by MSBuild; reference/compiler packages and external game/SDK assemblies are not
redistributed.

| Package | Version | Runtime asset | Retained notice |
| --- | --- | --- | --- |
| Google.Protobuf | 3.31.1 | lib/net45/Google.Protobuf.dll | google.protobuf/3.31.1/LICENSE |
| System.Memory | 4.5.3 | lib/netstandard2.0/System.Memory.dll | system.memory/4.5.3/ |
| System.Buffers | 4.4.0 | lib/netstandard2.0/System.Buffers.dll | system.buffers/4.4.0/ |
| System.Numerics.Vectors | 4.4.0 | lib/net46/System.Numerics.Vectors.dll | system.numerics.vectors/4.4.0/ |
| System.Runtime.CompilerServices.Unsafe | 4.5.2 | lib/netstandard2.0/System.Runtime.CompilerServices.Unsafe.dll | system.runtime.compilerservices.unsafe/4.5.2/ |

Google.Protobuf's NuGet metadata identifies source commit
`74211c0dfc2777318ab53c2cd2c317a2ef9012de`. Its BSD notice is copied verbatim
from [that revision's LICENSE](https://github.com/protocolbuffers/protobuf/blob/74211c0dfc2777318ab53c2cd2c317a2ef9012de/LICENSE).
The four System packages retain their verbatim NuGet `LICENSE.TXT` (as `LICENSE`)
and `THIRD-PARTY-NOTICES.TXT`. Their package versions and content hashes identify
the source of those notices; no replacement license text is synthesized.

Runtime DLLs live in `BridgeTools/RimGovernor` beside the Bridge assembly.
The inspected RimBridgeServer.dll SHA-256 is
`bdd0ad19036a3554eff4abeb2a5f35e4d13c0e8a4559985bdc13340589f913e0`.
Its `RimBridgeExtensionDiscovery.ResolveCompanionAssembly` searches the requesting
assembly's bundle directory before the BridgeTools root; `LoadScopedAssembly`
registers the same scope for loaded dependencies. This is a source-level loader
check. Native package acceptance must still exercise discovery and actual
Protobuf calls under the installed Mono/Unity runtime, including existing loaded
assembly/version interactions. The game supplies the netstandard framework
facade; the package does not replace framework assemblies.

The staged manifest records each runtime package/version/file and SHA-256 for
all bundled DLLs, notices and source. Existing headless and companion obligations
remain independently applicable.

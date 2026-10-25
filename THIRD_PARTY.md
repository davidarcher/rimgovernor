# Source and dependency notices

## NuGet dependencies

The whole repository is C# (.NET SDK 10). Pinned versions and the restore graph are in the
`packages.lock.json` files beside each project and in `brain/Directory.Packages.props`; restores run
in locked mode. Notable runtime dependencies are Microsoft.Data.Sqlite (the session journal, with the
native SQLite library shipped beside the mod as `e_sqlite3.dll`) and Microsoft.Web.WebView2 (the
launcher). The wire is plain C# records serialized with System.Text.Json (#3100); no Protocol Buffers
runtime ships. The build manifest records any bundled NuGet runtime dependencies and file hashes, and
actual native loading remains part of N01 package acceptance. Include applicable dependency notices
with distributed binaries.

## Native integration

- [Colony bridge source notice](integrations/rimgovernor-native/Notices/companion/PROVENANCE.md)
- [Vendored host (NativeServer, MIT) notices and commit SHAs](integrations/rimgovernor-native/Notices/host/PROVENANCE.md)
- [Headless adapter source notice](integrations/rimgovernor-native/Notices/headless/PROVENANCE.md) and GPL-3.0 license

Game files and artwork are supplied separately. The RimWorld 1.7 install (Waylith engine, its CoreCLR/net10 runtime, UnityStubs and Harmony 3 from the Workshop) is the game and Harmony's own and is neither bundled nor redistributed by the mod; the mod compiles against it. Encrypted remote-acceptance bundles of the install are described in [remote bundles](docs/developers/remote-bundles.md).

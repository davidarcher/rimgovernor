# Native operation envelope checks

Run against the private Harmony assembly used by the native mod:

```powershell
dotnet run --project contracts/tests/native-operation-envelope/NativeOperationEnvelopeTests.csproj -p:HarmonyAssembly=<absolute-path-to-0Harmony.dll>
```

The executable compiles the production envelope, attempt ledger and construction
hook verifier with official generated protobuf messages. It checks exact transport
limits, UTF-8 and JSON escaping, encodable immutable uncertain receipts and replay,
bounded progress, and live removal/replacement of actual Harmony patches. Only
the boundary limit constant is substituted; game and SDK assemblies are not needed.
These are protocol and patch metadata checks, not gameplay acceptance.

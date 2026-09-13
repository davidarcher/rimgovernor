# Typed native clock checks

Run `dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-clock`.
This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`.

The checks compile the production clock adapters, typed epoch runtime, event
projection, immutable journal, authority state, shared attempt ledger and
Protobuf boundary. Native watcher/game operations, registry access, authority
failure formatting and SDK transport use controlled seams. No game, HTTP server
or network listener starts. Journal fixtures remain under `.rimgovernor/`.

Coverage includes exact admission replay, ownership and cleanup after revocation,
monotonic lease behavior across a UTC jump, failed pause, initial safety stops,
event attribution, paging and missing history. Private game/SDK compilation and
Docker gameplay acceptance are separate requirements; these fixtures do not prove
Harmony hooks or actual native tick behavior.

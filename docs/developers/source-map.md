# Source map

[Architecture](architecture/overview.md) · [Developer guide](README.md)

| Source | Owns |
| --- | --- |
| [go/cmd/rimgovernor](../../go/cmd/rimgovernor) | Controller entry, configuration and lifecycle |
| [go/cmd/launcher](../../go/cmd/launcher) | Windows launcher, WebView2 UI, build/start/stop and API client |
| [go/internal/domain](../../go/internal/domain) | Shared identities, Concerns, Plans and actions |
| [go/internal/policy](../../go/internal/policy) | Pure decisions, demand, forecasts, placement and tactics |
| [go/internal/buildingruntime](../../go/internal/buildingruntime) | Rounds, planner catalog/queue and runtime composition |
| [go/internal/store](../../go/internal/store) | Admission, session journal and views rebuilt from saved intent |
| [go/internal/executor](../../go/internal/executor) | Hands dispatch and reconciliation |
| [go/internal/facts](../../go/internal/facts), [observation](../../go/internal/observation) | Fact access and native-read decoding |
| [go/internal/bridge](../../go/internal/bridge) | Typed native boundary and guarded operations |
| [go/internal/gabp](../../go/internal/gabp), [gamehost](../../go/internal/gamehost) | Transport and game process connection |
| [go/internal/httpapi](../../go/internal/httpapi) | Player API, lifecycle, reads, media and diagnostics |
| [go/internal/supplysim](../../go/internal/supplysim) | Seeded offline stock-flow scenarios; real planner adapters live in buildingruntime tests |
| [go/internal/snapshot](../../go/internal/snapshot) | Recorded-fact planner regression tests |
| [go/internal/nativeaccept](../../go/internal/nativeaccept) | Case registry, fixtures, runner and gameplay evidence |
| [go/internal/archgate](../../go/internal/archgate) | Architecture gates and shrink-only baselines |
| [integrations/rimgovernor-native/src/Bridge](../../integrations/rimgovernor-native/src/Bridge) | Colony observations, operations, supervision and rendering |
| [integrations/rimgovernor-host/src](../../integrations/rimgovernor-host/src) | GABP server, tool discovery, lifecycle tools and main-thread dispatch |
| [contracts](../../contracts) | Wire schemas, generated types and protocol probes |
| [scripts/fixtures](../../scripts/fixtures) | Native test fixture implementations |

The host is a vendored fork of RimBridgeServer and Lib.GAB. Keep licensing and
upstream attribution in its [provenance](../../integrations/rimgovernor-native/Notices/host/PROVENANCE.md),
not in architecture change histories.

## Find a behavior

Start with the [subsystem contract index](contracts/README.md). For a runtime
path, follow the planner catalog to its planner, then its pure policy function
and registered executor handler. For a wire change, follow the schema through
generated Go/C# types to both adapters.

Use [persistence ownership](contracts/persistence-contracts.md) to place state
and [flight rows](contracts/flight-rows.md) to add diagnostics. Avoid creating a
second owner merely because the code is in a different package.

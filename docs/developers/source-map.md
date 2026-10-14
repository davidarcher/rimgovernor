# Source map

[Architecture](architecture/overview.md) · [Developer guide](README.md)

| Source | Owns |
| --- | --- |
| [brain/src/RimGovernor.Brain.Host](../../brain/src/RimGovernor.Brain.Host) | In-process brain host: lifecycle, scheduler thread, watchdog, composition and the facade client ([brain host](architecture/brain-host.md)) |
| [brain/src/RimGovernor.Brain.Domain](../../brain/src/RimGovernor.Brain.Domain) | Shared identities, Concerns, Plans and actions |
| [brain/src/RimGovernor.Brain.Policy](../../brain/src/RimGovernor.Brain.Policy) | Pure decisions, demand, forecasts, placement and tactics |
| [brain/src/RimGovernor.Brain.BuildingRuntime](../../brain/src/RimGovernor.Brain.BuildingRuntime) | Rounds, planner catalog/queue and runtime composition |
| [brain/src/RimGovernor.Brain.Store](../../brain/src/RimGovernor.Brain.Store) | Admission, session journal and views rebuilt from saved intent |
| [brain/src/RimGovernor.Brain.Executor](../../brain/src/RimGovernor.Brain.Executor) | Hands dispatch and reconciliation |
| [brain/src/RimGovernor.Brain.Facts](../../brain/src/RimGovernor.Brain.Facts), [Observation](../../brain/src/RimGovernor.Brain.Observation) | Fact access and native-read decoding |
| [brain/src/RimGovernor.Brain.Bridge](../../brain/src/RimGovernor.Brain.Bridge), [Bridge.Contracts](../../brain/src/RimGovernor.Brain.Bridge.Contracts) | Typed native boundary, definition catalog and guarded operations |
| [brain/src/RimGovernor.Brain.NativeFacade](../../brain/src/RimGovernor.Brain.NativeFacade), [HttpChannel](../../brain/src/RimGovernor.Brain.HttpChannel) | The in-process native facade and the loopback HTTP channel ([native facade](architecture/native-facade.md), [HTTP channel](architecture/http-channel.md)) |
| [brain/src/RimGovernor.Brain.Contracts](../../brain/src/RimGovernor.Brain.Contracts) | Wire records (the C# source of truth) and their System.Text.Json context |
| [brain/src/RimGovernor.Brain.PlayerApi](../../brain/src/RimGovernor.Brain.PlayerApi) | Player API, lifecycle, reads, media and diagnostics |
| [brain/src/RimGovernor.Brain.Simulation](../../brain/src/RimGovernor.Brain.Simulation) | Seeded offline stock-flow scenarios |
| [brain/src/RimGovernor.Brain.Snapshot](../../brain/src/RimGovernor.Brain.Snapshot) | Recorded-fact planner regression support |
| [brain/src/RimGovernor.Brain.Telemetry](../../brain/src/RimGovernor.Brain.Telemetry) | OTLP file telemetry: decisions, events, spans and metrics |
| [brain/src/RimGovernor.Brain.Acceptance](../../brain/src/RimGovernor.Brain.Acceptance), [Acceptance.Cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases) | Acceptance harness (`RimGovernor.cmd acceptance`), case registry, fixtures and gameplay evidence |
| [brain/src/RimGovernor.Brain.RemoteAccept](../../brain/src/RimGovernor.Brain.RemoteAccept), [RemoteBundle](../../brain/src/RimGovernor.Brain.RemoteBundle) | Remote acceptance evidence and encrypted bundles |
| [brain/analyzers](../../brain/analyzers) | Architecture gates: Roslyn analyzers `RGARCH001`-`008` and their shrink-only baselines |
| [brain/tests](../../brain/tests) | Bug-driven xUnit tests |
| [tools/rg](../../tools/rg) | Repository tool host behind `RimGovernor.cmd` (land, test, issue, affected, acceptance, remote tools, reports) |
| [tools/launcher](../../tools/launcher) | Windows launcher (C#, WinForms and WebView2): setup, game start/stop, new colony, acceptance tab |
| [tools/observability](../../tools/observability) | Optional Grafana/OTLP stack and dashboards |
| [integrations/rimgovernor-native/src/Bridge](../../integrations/rimgovernor-native/src/Bridge) | Colony observations, operations, supervision and rendering |
| [integrations/rimgovernor-host/src](../../integrations/rimgovernor-host/src) | Vendored RimBridgeServer fork: tool registration, lifecycle tools and main-thread dispatch (may fold into the mod, #3141) |
| [contracts](../../contracts) | Wire contract semantics and native contract probes |
| [scripts/fixtures](../../scripts/fixtures) | Native test fixture implementations |

The host is a vendored fork of RimBridgeServer. Keep licensing and
upstream attribution in its [provenance](../../integrations/rimgovernor-native/Notices/host/PROVENANCE.md),
not in architecture change histories.
## Find a behavior

Start with the [subsystem contract index](contracts/README.md). For a runtime
path, follow the planner catalog to its planner, then its pure policy function
and registered executor handler. For a wire change, follow the record in
`Brain.Contracts` to the native handler and the facade client.

Use [persistence ownership](contracts/persistence-contracts.md) to place state
and [OTEL signals](contracts/otel-signals.md) to add diagnostics. Avoid creating a
second owner merely because the code is in a different package.

# Source map

[Architecture](architecture/overview.md) · [Developer guide](README.md)

| Source | Owns |
| --- | --- |
| [brain/src/RimGovernor.Brain.Host](../../brain/src/RimGovernor.Brain.Host) | In-process brain host: lifecycle, scheduler thread, watchdog, composition and the facade client ([brain host](architecture/brain-host.md)) |
| [brain/src/RimGovernor.Brain.Domain](../../brain/src/RimGovernor.Brain.Domain) | Shared identities, Plans and actions |
| [brain/src/RimGovernor.Brain.Policy](../../brain/src/RimGovernor.Brain.Policy) | Pure decisions, demand, forecasts, placement and tactics |
| [brain/src/RimGovernor.Brain.Runtime](../../brain/src/RimGovernor.Brain.Runtime) | Colony review, planner catalog/queue, planning recordings/replay and runtime composition |
| [brain/src/RimGovernor.Brain.Store](../../brain/src/RimGovernor.Brain.Store) | Admission, session journal and views rebuilt from saved intent |
| [brain/src/RimGovernor.Brain.Executor](../../brain/src/RimGovernor.Brain.Executor) | Hands execution of accepted actions, native dispatch and reconciliation |
| [brain/src/RimGovernor.Brain.Bridge](../../brain/src/RimGovernor.Brain.Bridge) | Fact access and native-read decoding |
| [brain/src/RimGovernor.Brain.Bridge](../../brain/src/RimGovernor.Brain.Bridge), [GameAccess](../../brain/src/RimGovernor.Brain.GameAccess) | Typed native boundary (including the in-process native facade), definition catalog and guarded operations |
| [brain/src/RimGovernor.Brain.HttpChannel](../../brain/src/RimGovernor.Brain.HttpChannel) | The loopback HTTP channel ([game API](architecture/game-api.md), [HTTP channel](architecture/http-channel.md)) |
| [brain/src/RimGovernor.Brain.GameFacts](../../brain/src/RimGovernor.Brain.GameFacts) | Native observations and outcomes, shared values and their strict generated JSON context |
| [brain/src/RimGovernor.Brain.GameOrders](../../brain/src/RimGovernor.Brain.GameOrders) | Requests and intent, referencing Facts, and their strict generated JSON context |
| [brain/src/RimGovernor.Brain.PlayerApi](../../brain/src/RimGovernor.Brain.PlayerApi) | Player API, lifecycle, reads, media and diagnostics |
| [brain/src/RimGovernor.Brain.Simulation](../../brain/src/RimGovernor.Brain.Simulation) | Seeded offline stock-flow scenarios |
| [brain/src/RimGovernor.Brain.Telemetry](../../brain/src/RimGovernor.Brain.Telemetry) | OTLP file telemetry: decisions, events, spans and metrics |
| [brain/src/RimGovernor.Brain.Acceptance](../../brain/src/RimGovernor.Brain.Acceptance), [Acceptance.Cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases) | Acceptance harness (`RimGovernor.cmd acceptance`), case registry, fixtures and gameplay evidence |
| [brain/src/RimGovernor.Fixtures.Contracts](../../brain/src/RimGovernor.Fixtures.Contracts) | The typed stage spec and reply of `test/stage`: the harness references it, fixture builds of the mod compile it in, production never does |
| [brain/src/RimGovernor.Brain.RemoteAccept](../../brain/src/RimGovernor.Brain.RemoteAccept), [RemoteBundle](../../brain/src/RimGovernor.Brain.RemoteBundle) | Remote acceptance evidence and encrypted bundles |
| [brain/analyzers](../../brain/analyzers) | Architecture gates: Roslyn analyzers `RGARCH001`-`010`; baselined gates are shrink-only, construction tiers and live Method stages have no baseline |
| [brain/tests](../../brain/tests) | Bug-driven xUnit tests |
| [tools/repo](../../tools/repo) | Front door behind `RimGovernor.cmd`: land, test (the gate), issue, affected; no brain references ([tools/common](../../tools/common) holds what both tools share) |
| [tools/repo.Tests](../../tools/repo.Tests) | The landing lane against throwaway git repositories |
| [tools/rg](../../tools/rg) | Brain tool host the front door forwards to: acceptance, remote tools, reports |
| [tools/launcher](../../tools/launcher) | Windows launcher (C#, WinForms and WebView2): setup, game start/stop, new colony, acceptance tab |
| [integrations/rimgovernor-native/src/Bridge](../../integrations/rimgovernor-native/src/Bridge) | Colony observations, operations, supervision and rendering |
| [integrations/rimgovernor-host/src](../../integrations/rimgovernor-host/src) | Vendored NativeServer fork: tool registration, lifecycle tools and main-thread dispatch (may fold into the mod, #3141) |
| [integrations/rimgovernor-realcombat](../../integrations/rimgovernor-realcombat) | RimWorld's own combat run headless in a bare process, with the mod's combat mirror and order handler hosted over it (#3142), and the parity tool that compares it with the hand-ported sim |
| [contracts](../../contracts) | Wire contract semantics and native contract probes |
| [scripts/fixtures](../../scripts/fixtures) | Native test fixture implementations |

The host is a vendored fork of NativeServer. Keep licensing and
upstream attribution in its [provenance](../../integrations/rimgovernor-native/Notices/host/PROVENANCE.md),
not in architecture change histories.
## Find a behavior

Start with the [subsystem contract index](contracts/README.md). For a runtime
path, follow the planner catalog to its planner, then its pure policy function
and registered executor handler. For a wire change, follow the record in
`Brain.GameFacts` to the native handler and the facade client.

Use [persistence ownership](contracts/persistence-contracts.md) to place state
and [OTEL signals](contracts/otel-signals.md) to add diagnostics. Avoid creating a
second owner merely because the code is in a different package.

For live food Method selection, follow the [policy stage map](architecture/colony-forecast.md#live-activity-policy-stages):
family generation and binding live under `Policy/Methods`, while comparison uses
the existing ColonyForecast outcome evaluator. `Runtime` sequences the
reads and journal transitions; `Store` and Hands retain admission and execution.

# Native mutable static-state inventory

Source baseline: `4d8edbc3`. This is the semantic state portion of
[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22), complementing the
[saved-field audit](native-state-ownership.md). Current behavior below is established
by source inspection, not native execution. Hazards are acceptance requirements;
their implementation queue remains N01.05 in the backlog. No runtime or
packaging change is implied by this audit. Sources now live in the unified native
root; excluded duplicate helpers have been deleted. Historical compatibility
requirements in the audit are not active gates; current-session lifecycle hazards
remain N01.05 work.

Rows below that say "verify repeated game reuse" name the hazard the
opt-in reusable-game acceptance lifecycle
([nativeaccept.GameReuse](../go/internal/nativeaccept/reuse.go), exercised by
the `lifecycle/reuse` acceptance case) does *not* clear: a reload into the same process leaves every
static in this table untouched. `lifecycle/reuse` proves the load-token /
authority / draft / stock reset contract only; a case asserting on one of
these statics runs in fresh-process mode.

## Coverage method

All 86 C# files under the two integration source trees were parsed with Roslyn
`CSharpSyntaxTree.ParseText`. Each `FieldDeclarationSyntax` with a `static` modifier
was expanded into its individual variables, including declarations without access
modifiers, multiple variables and `readonly` containers. Both default preprocessing
and `THROUGHPUT_FIXTURE` were inspected. Default sources contain 131 static field
declarations including nine in excluded duplicate files; the fixture symbol adds
`RenderDemandDriver.TestSuspendUntil`. Seven static properties are computed getters,
not additional storage; no static event storage was found. Constants, methods and
static constructors were not mistaken for fields.

The tables classify every field, including synchronization objects, lazy reflection
handles and reference-held mutable objects. `readonly` only prevents rebinding; it
does not make dictionaries, arrays, sessions or Unity objects immutable. Static
singletons' instance-owned resources are described where they determine cleanup.
The final metadata section explicitly separates references/collections with no
runtime writes found from active mutable state. The saved identity assembly has
no declared static fields. No headless source declares static fields; its writes
to Unity state and Harmony registration are covered separately.

All unqualified owning classes below are in `HomeBridge.BridgeTools`. Field groups
share the stated lifetime and rules. “Process” means the loaded managed assembly's
lifetime; none of these static fields is serialized. A documented guard or reset
does not establish that every native interruption path reaches it.

## SDK caches and definition caches

| Owner and actual fields | Initialization, reset and lifetime | Disconnect, player direction and unresolved acceptance | N01 owner |
| --- | --- | --- | --- |
| [BridgeCommon](../integrations/rimgovernor-native/src/Bridge/BridgeCommon.cs): `CacheGate`, `DeclaredByToolName` | Process lock and initially empty dictionary. `DeclaredParameters` caches arrays by tool name after reflection, including an empty result after errors. No eviction/reset; returned arrays are shared. | Reads require no connection lease and hold no player authority. Verify duplicate tool names/types and premature discovery cannot permanently cache the wrong signature. Caller mutation of returned arrays is possible by type; no such write was found. | N01.02 SDK boundary |
| `BridgeCommon`: `_journalProbed`, `_journalProperty` | One process-wide lazy probe under `CacheGate`; sets probed before finding the SDK assembly/property, including missing/failed lookup. No retry/reset. | A missing journal remains unavailable even if the assembly appears later. Test discovery order, missing raw arguments and fail-closed guarded writes; do not silently reinterpret a cached miss as validated input. | N01.02 SDK boundary |
| [PawnSettingsRead in PawnConfigTool.cs](../integrations/rimgovernor-native/src/Bridge/PawnConfigTool.cs): `LetterGate`, `_byLetter`, `_letterOf` | Process lock and lazily populated bidirectional time-assignment definition maps. `EnsureLetters` runs once; an unavailable definition database can become an empty cached mapping. | No load/map/lease reset; player schedule changes do not alter the mapping. Verify initialization after definitions are ready and repeated game reuse with the same loaded mod set. Dynamic definition replacement would require invalidation rather than stale Def references. | N01.03 observations / N01.04 pawn settings |

## Execution and safety supervision

| Owner and actual fields | Initialization, reset and lifetime | Disconnect/player behavior and stale-state hazard | N01 owner |
| --- | --- | --- | --- |
| [Supervisor](../integrations/rimgovernor-native/src/Bridge/SupervisedPlayTool.cs): `Gate`, `_state` | Process lock, one current `State`; `Start` creates epoch state capturing Game/Map, clock settings, lease, tick bounds and safety baselines. `Stop` makes it inactive but retains the object for status; later start replaces it. Failed pause keeps it active for another frame. | Frame/tick callbacks stop on expiry, identity/map change, rewind, danger or external clock change. A changed game is retired without pausing the newly loaded game. Verify shutdown/failed callbacks do not strand active authority and stale status is never read as fresh admission. | N01.05 supervision |
| `Supervisor`: `Journal`, `_epoch`, `_cursor` | Lazy process `ClockEventJournal` bound to the first native profile directory. Initialization reads durable newest cursor and seeds epoch; starts increment epoch, append advances cursor. No journal reset/dispose API; its file streams are scoped per append/read. | Journal errors pause the matching game, restore boost and retire state. Journal persists across loads; rows carry colony/load/map identity. Test profile reuse, pending publication recovery, gaps, stale-load event filtering and unbounded retained files. This is the existing clock journal, not the proposed construction/haul delivery protocol. | N01.05 journal / N01.06 recovery |
| `Supervisor`: `_patched`, `_patchError` | Interlocked installation gate, error text; installation verifies update/tick Harmony owners and resets gate on exception. Process lifetime after success. | Does not periodically revalidate patch presence. Partial installation followed by retry needs idempotence/health acceptance. Missing required guard must disable capability, including after external patch changes. | N01.05 patch health |
| `Supervisor`: `InjuryStops`, `_injuryStopsSession`, `_priorEpochConsciousHostiles` | Process-held memory intentionally spans epochs. `Start` clears all three on changed Game identity; injury entries older than one hour are pruned when recording stops. Hostile count updates at probes. | Not map-scoped: switching maps in the same Game retains the prior count and injury suppression. Verify map switch, return, changed pawn set and same-Game rewind cannot suppress new danger or invent a threat-cleared transition. No explicit player reset beyond new-game baseline. | N01.05 safety observations |
| [HomePlayUntilEventTools](../integrations/rimgovernor-native/src/Bridge/PlayUntilEventTool.cs): `_running` | Interlocked single invocation gate, cleared in outer `finally` together with injury disarm. Conflicts with active supervisor are refused. | Loop reacts to cancellation, unavailable/changed Game and clock events. Verify cancellation with an outstanding main-thread hop and concurrent supervisor admission; the two separate gates are not a combined atomic owner. | N01.05 clock admission |
| `HomePlayUntilEventTools`: `AlertMemoLock`, `AlertLastCountedMs`, `AlertMemoClock`, `_alertMemoGame`, `_alertMemoTick` | Process lock, dictionary and running Stopwatch; Game change or tick rewind clears alert memo during `SyncAlertMemoToGame`. Stored timestamps debounce alerts across short invocations. Clock itself never resets/stops. | No map reset or ordinary disconnect clearing. Test same-game map switch, pause/reconnect and changing alert keys; dictionary has no size-based eviction. Debounce must not become permanent acknowledgement of a new danger. | N01.05 safety observations |
| [CombatInjuryHook](../integrations/rimgovernor-native/src/Bridge/CombatInjuryHook.cs): `Gate`, `_armed`, `_first` | Process lock; `Arm` installs pawn-ID set and clears first event, `Disarm` clears both. Reference comparison to the exact arm prevents an older damage callback from publishing into a newer arm. | An armed injury immediately pauses. Arm state itself carries pawn IDs, not Game/Map identity; outer short-loop `finally` disarms. Verify load between arming and next loop probe cannot let matching pawn IDs pause another game. A lost caller must still reach disarm. | N01.05 injury guard |
| `CombatInjuryHook`: `_patched`, `_target`, `_patchError` | Process installation gate and reflected target; validates patch owner, resets gate on failure, retains diagnostic target/error. | No automatic unload/revalidation on player changes. Test partial installation/retry and removal of an installed patch before admitting injury-guarded play. | N01.05 patch health |
| `CombatInjuryHook`: `_prefixCalls`, `_watchedCalls`, `_injuryChanges`, `_hookErrors` | Interlocked cumulative process diagnostics; no load/arm reset. | Read as cumulative counts, never evidence of this load's completed pawn work. Verify consumers do not infer current guard health solely from historical nonzero counters. | N01.05 diagnostics |
| [LetterPauseHook](../integrations/rimgovernor-native/src/Bridge/LetterPauseHook.cs): `depth`, `current` | Synchronous receive-letter nesting state. Prefix increments/sets current; finalizer decrements and clears at zero. Not thread-local and not a stack of prior letters. | Finalizer covers exceptions. Verify nested ReceiveLetter callbacks: returning from an inner letter does not restore the outer `current` while depth stays positive. Also verify callback thread assumptions. | N01.05 pause attribution |
| `LetterPauseHook`: `game`, `letterId` | Latest pause attribution, assigned only by a paused clock transition inside a letter scope. Any subsequent speed callback clears it; `Consume` checks Game reference and clears. | Player/non-letter clock change invalidates the attribution. Old Game reference can survive until a speed callback/consume but cannot match another Game. Test same-Game rewind and nested letter attribution. | N01.05 pause attribution |

`Supervisor`'s `BoostField` is immutable reflection metadata (listed below), but
`RestoreBoost` writes the external static `TickManager.UltraSpeedBoost` from the
epoch's saved prior value. Source restoration does not compare the current boost
with the value the epoch installed. Verify another owner changing boost and
load-change cleanup; test acceleration must not become player policy.

## Ownership observations and synchronous transition state

| Owner and actual fields | Initialization/reset/lifetime | Disconnect/player behavior and unresolved acceptance | N01 owner |
| --- | --- | --- | --- |
| [DraftOwnership](../integrations/rimgovernor-native/src/Bridge/DraftOwnership.cs): `Claims` | Process `ConditionalWeakTable` keyed by exact pawn draft-controller object. `Acquire` stores owner after a draft; every patched draft setter removes the claim. No explicit bulk reset; dead keys can be collected. | Claims are unsaved; no autonomous disconnect undraft/expiry here. Controller cleanup must recheck exact ownership; a later setter, including same-value, wins. Test retained old pawn references and load/reconnect; weak-key lifetime is not a lease. | N01.04 draft writes / N01.05 cleanup |
| [OrderedWorkHistory](../integrations/rimgovernor-native/src/Bridge/OrderedWorkHistory.cs): `game`, `generations` | Process dictionary by pawn numeric ID; `Read` resets on changed Game, otherwise increments for non-owned orders/toggles. No pruning within a Game. | Player input invalidates generation. Same-Game rewind/map changes do not reset it; callers must separately bind load/map. Verify repeated game reuse and order paths not covered by `TryTakeOrderedJob`/draft gizmos. | N01.04 ownership evidence |
| `OrderedWorkHistory`: `[ThreadStatic] owned` | Per-thread nesting count incremented by `Owned`, decremented by `Scope.Dispose`. Only synchronous same-thread scope is intended. | No load reset or defensive idempotent dispose; crossing await/thread boundaries or double disposal misclassifies later player orders. Verify all callers use a single synchronous `using` and exceptions release it. | N01.04 main-thread admission |
| [ConstructionLineage](../integrations/rimgovernor-native/src/Bridge/ConstructionLineage.cs): `completions` | Process list of synchronous Frame completion scopes. Prefix adds; finalizer removes, retaining records/frame/spawned candidates only within a transition. | Installed hooks continue when disconnected; saved history is owned separately. Verify nested completion, exceptions and game transitions leave no scopes; the list has no independent reset or synchronization. | N01.06 transition evidence |
| [HaulTracking](../integrations/rimgovernor-native/src/Bridge/HaulTracking.cs): `merges` | Process list of synchronous stack-transfer scopes; prefix adds and finalizer removes. Used to distinguish destruction during absorption from lost stock. | Continues when disconnected. Verify nested/reentrant transfer and exception cleanup; leaked scope would suppress later destruction evidence. No Game-keyed reset or independent capacity exists for this temporary list. | N01.06 quantity evidence |
| [WallUpgradeSafety](../integrations/rimgovernor-native/src/Bridge/WallUpgradeTool.cs): `issuing` | Process bool true only around synchronous designation, reset in `finally`. | Makes the ownership hook ignore the bridge's own designation. No nesting counter/thread identity: test reentrant designation and external setter inside the call. Never carry it through queued work. | N01.04 wall operations |
| [HomeCoverage](../integrations/rimgovernor-native/src/Bridge/HomeCoverageTool.cs): `PreparingFixture` | Production-compiled bool initialized false; only writers found in `scripts/fixtures/HomeCoverageFixture.cs`, each with `finally` reset. | Suppresses the Home revision-bump hooks while true so fixture setup does not count as a geometry change. It is not a production authority input. Verify fixture failures restore false and production packaging excludes fixture tools; consider compile-gating the field with its only writers. | N01.01 fixture isolation / N01.04 Home |

These classes also own process installation flags. They are **separate fields**
from their saved components or transient work scopes:

| Class and flag | Entry/reset behavior | Acceptance and family |
| --- | --- | --- |
| `DraftOwnership.patched` | `Ensure`; explicit draft-setter lookup, true after patch, no reset after success. | Setter/owner health and repeated install, N01.05. |
| `OrderedWorkHistory.installed` | `Read`; two patches, exceptions return unavailable, true only after both. | Failure after first patch and retry; unknown generation must remain unknown, N01.04/05. |
| `ConstructionLineage.installed` | `Install`; multiple transition patches, true after last, no reset. | Partial patch failure and retry without duplicate transitions, N01.05/06. |
| `HaulTracking.installed` | `Install`; merge/split/destroy/spawn patches, true after last, no reset. | Partial patch failure and retry without double quantity accounting, N01.05/06. |
| [DrillingGuard](../integrations/rimgovernor-native/src/Bridge/DrillingGuard.cs): `patched` | `Install`; eligibility/work/transition patches, true after last, no reset. | Missing required work/transition hook must disable admission, N01.04/05. |
| [MiningGuard](../integrations/rimgovernor-native/src/Bridge/MiningGuard.cs): `patched` | `Install`; explicit DoDamage check plus designation removal patch, true after both. | Retry after partial install; no duplicate recovered output, N01.04/05. |
| [ProductionPolicyGuard](../integrations/rimgovernor-native/src/Bridge/ProductionPolicyTool.cs): `patched` | `EnsurePatched`; ingredient selection and consumption hooks, true after both. | Missing consumption guard cannot leave apparently healthy policy, N01.04/05. |
| [RecoveryAreaOwnership](../integrations/rimgovernor-native/src/Bridge/RecoveryAreaOwnership.cs): `patched` | `Ensure`; area setter patch then true, no reset. | Player override detection unavailable if patch absent; test health, N01.04/05. |
| `HomeCoverage.installed` | `Install`; Set/Clear/Invert then true, no reset. | Partial revision observation must not be treated as complete, N01.04/05. |
| `WallUpgradeSafety.installed` | `Install`; also initializes PlayerFrame UI observer; true after all hooks. | Partial guard install and repeat initialization, N01.04/05. |
| `LetterPauseHook.installed` | `EnsurePatched`; letter and three clock callbacks then true, no reset. | Partial installation/retry must preserve pause attribution, N01.05. |

These flags persist across disconnect/load/player override because Harmony patches
are process infrastructure. They must not be mistaken for per-colony authority.
Except where explicitly described, success is not rechecked against Harmony's
current patch set. N01.05 owns capability health and idempotent retry acceptance.

## Presentation and explicit player input

| Owner and actual fields | Initialization/reset/lifetime | Disconnect/player behavior and unresolved acceptance | N01 owner |
| --- | --- | --- | --- |
| [PlayerFrame](../integrations/rimgovernor-native/src/Bridge/PlayerInputTool.cs): `uiRevision`, `observingUi` | Process revision counter and one-time Root.OnGUI installation flag; qualifying mouse/keyboard events increment revision. No load reset. | Player input invalidates old frame revision. Test overflow/long reuse, missing patch and same-value UI state; revision alone is insufficient without frame Game/Map checks. | N01.05 input freshness |
| `PlayerFrame`: `Frames`, `Source` | Process dictionary and stream identifier; `Remember` clears when source changes, retains sequence values no older than current sequence minus 90. Each frame holds Game/Map and UI/camera snapshots. | Disconnected frames remain until new frames arrive. `SameScene`/`Matches` reject other Game/Map and changed camera/windows; player override remains separate revision/selection evidence. Verify source replacement, sequence reset and stale-frame release; old game references remain bounded only under monotonic source sequences. | N01.05 frame/input |
| [PrivatePlayerInput](../integrations/rimgovernor-native/src/Bridge/PlayerInputTool.cs): `instance` | Lazy process-persistent MonoBehaviour owning X display, lease/owner/order, held keys/buttons and shared-memory channel. Init failure destroys/nulls; OnDestroy releases controls, closes/disposes resources, deletes channel and nulls. | Eight-second renewed lease; Update releases on expiry or changed Game/Map. Release deselects an unfinished designation for matching Game before key/button ups. Explicit owner/source/frame/order checks apply. Test thrown native release, lost callback, map switch during drag and shutdown; instance-held Game/Map references are not cleared by ordinary release. | N01.05 explicit input |
| [RenderDemandDriver](../integrations/rimgovernor-native/src/Bridge/RenderDemandTool.cs): `instance`, `until`, `Suspended` | Lazy persistent MonoBehaviour and map-draw patch; max-extension lease, quarter-second update. No Game/Map binding. OnDestroy clears suspension/restores cameras, but does not explicitly zero `until` or null `instance` (Unity destroyed-object semantics apply). | Visible/unknown window state renders; batch refuses. Expiry can suspend hidden-window cameras, remembered in an instance set. Restore re-enables those cameras without checking later player/other-mod changes. Test cross-load lease, object destruction/recreation, camera replacement and lost controller; no speed changes are intended. | N01.05 render lease |
| `RenderDemandDriver`: `TestSuspendUntil` | Exists only with `THROUGHPUT_FIXTURE`; process absolute real-time deadline checked in Apply. | Fixture can force suspension on Linux independently of window APIs; production default parse has no field. Verify separately identified fixture artifacts and expiry. | N01.01 fixtures / N01.05 presentation |
| [VideoStreamDriver](../integrations/rimgovernor-native/src/Bridge/VideoStreamTool.cs): `instance` | Lazy persistent MonoBehaviour owns mapping/file/mutex, textures, GPU pending flag, sequence and lease. Release disposes resources; OnDestroy nulls. Outstanding GPU target waits for callback before release. | Lease <=15 seconds; zero explicitly stops; expiry/errors release. Current-screen video intentionally crosses map/load; frames must remain evidence only. Release restores saved vsync/frame rate without comparing later settings. Test destroy during pending GPU callback, lost controller and player setting change; an old callback must not use a new mapping generation. | N01.05 video/platform |
| [PawnImageCapture](../integrations/rimgovernor-native/src/Bridge/PawnImageTool.cs): `instance` | Lazy persistent MonoBehaviour installs draw/view hooks. No OnDestroy cleanup or explicit patch-registration flag beyond instance exists. | Verify destruction/shutdown while pending completes/refuses request and repeated creation does not duplicate patches. | N01.05 portrait lifecycle |
| `PawnImageCapture`: `pending`, `pawnId`, `sessionId`, `view`, `pawn`, `deadline`, `extraView` | One static request, four-second timeout; Begin refuses busy and captures session. Draw revalidates session/pawn; success/error clears pending/pawn/extraView; finalizer clears extraView. ID/view/deadline values remain inert until replaced. | Pending capture continues after initial queued Begin; operation cancellation is not passed to the pending task. Timeout relies on Update. Temporary camera/render target changes are synchronous and restored in finally. Test queued cancellation, destroy/no Update, load during request, render exceptions and concurrent player view. | N01.05 read-only capture |
| [Watch](../integrations/rimgovernor-native/src/Bridge/Watch.cs): `Sync`, `Current` | Process lock and one decorative Session. New Open closes previous; Finish schedules bounded delayed close through main-thread queue with CancellationToken.None. Close marks session closed and clears Current if exact match. | Cleanup compares current tab type/Def and selected object, preserving visibly different player choices. Session has no Game/Map or UI revision: same tab in a new load or player close/reopen can match old cleanup. No shutdown drain; disconnected delay continues. Test cancellation before Finish, load before close and same-tab player override. Camera movement is not restored by Watch. | N01.05 watch/UI lifecycle |

## Trade session and delayed dialog ownership

Source: [TradeTool.cs](../integrations/rimgovernor-native/src/Bridge/TradeTool.cs), class
`HomeTradeTools`. The fields below are process storage for a session, not saved
authority. N01.04 owns trade operations; N01.05 owns delayed UI cleanup.

| Actual fields | Lifetime/reset | Disconnect/player behavior and unresolved acceptance |
| --- | --- | --- |
| `_running` | Interlocked one-call gate, released in outer finally after scheduling emergency dialog cleanup. | Cleanup is queued without awaiting completion before gate release. Test a second trade call before previous cleanup callback runs. |
| `_sessionTraderId`, `_sessionTraderName`, `_sessionNegotiatorName`, `_sessionOpenedTick`, `_sessionOpenedByUs`, `_sessionId` | Open fills receipt/presentation identity, generating GUID; CloseSession clears. No lease or independent load callback. | Disconnect retains staged state until explicit close/new session. RequireSession checks live identity before effects; stale metadata can still be emitted by AddSessionBlock. Verify stale status is not interpreted as executable authority. |
| `_sessionMap`, `_sessionIdentity`, `_sessionDeal`, `_sessionTrader`, `_sessionNegotiator` | Open captures exact object identities; CloseSession clears references. RequireSession compares current colony component/map and native TradeSession objects. | Replaced player session or load fails closed; no automatic expiry/clear. Test disconnect, trader departure, old/new load and map return, preserving a player-opened trade. Retained references can prolong old Game objects. |
| `_requireAdjacent` | Set on open; not reset in CloseSession, inert until next open/session validation. | Verify old adjacency configuration cannot bypass fresh session admission; disconnected staged trades still require current eligibility. |
| `_ourTradeDialog`, `_ourDialogSignature` | Tool-owned window and staged contents signature. Normal and emergency close clear both; missing dispatcher clears references without closing window. | Signature detects changed player offer before acceptance. Emergency closure reads global current dialog when delayed callback runs, rather than capturing prior dialog identity. Test new dialog opened before old cleanup callback, player edits, cancelled caller and failed dispatcher; preserve newer UI ownership. |

## Readonly metadata and lookup collections

These declared statics have no runtime mutation sites in the inspected source,
apart from initialization. They have process lifetime, no disconnect/load cleanup
and no player authority of their own. Reflection handles may still be null or
point to mutable **game-owned** data; their consumers must validate that boundary.
Arrays/sets below are mutable types despite current lookup-only use. N01.02/03/04
own typed boundary migration; N01.05 owns relevant watcher metadata health.

| Source / class | Fields and classification |
| --- | --- |
| [BuildingConfigTool.cs](../integrations/rimgovernor-native/src/Bridge/BuildingConfigTool.cs), `HomeBuildingConfigTools` | `WantSwitchOnField`: readonly reflection handle. |
| [CellsPlusTool.cs](../integrations/rimgovernor-native/src/Bridge/CellsPlusTool.cs), `HomeMapTools` | `CellFieldNames`, `ThingFieldNames`: initialized lookup arrays. |
| `CellsPlusTool.cs`, `ReferenceComparer<T>` | `Instance`: stateless comparer singleton; no mutable instance fields. |
| [DialogTextTool.cs](../integrations/rimgovernor-native/src/Bridge/DialogTextTool.cs), `HomeDialogTextTools` | `PreferredFieldNames`, `ContextTextFieldNames`, `ContextLabelFieldNames`: lookup arrays. |
| [ListPawnsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListPawnsTool.cs), `HomePawnTools` | `SituationalCacheField`, `SituationalDirtyField`: readonly reflection handles; observed caches belong to pawns. |
| [OrderTool.cs](../integrations/rimgovernor-native/src/Bridge/OrderTool.cs), `HomeOrderTools` | `Actions`, `Modes`: lookup arrays. |
| `PawnConfigTool.cs`, `PawnSettingsRead` | `PrioritiesField`, `TrainingGetSteps`, `EggProgressField`, `SocialCacheField`, `SocialActiveField`: readonly reflected fields/method. |
| [PlaceBuildingTool.cs](../integrations/rimgovernor-native/src/Bridge/PlaceBuildingTool.cs), `HomePlaceBuildingTools` | `RotationNames`: lookup array. |
| `PlayUntilEventTool.cs`, `HomePlayUntilEventTools` | `ActiveAlertsField`, `LiveMessagesField`: readonly watcher handles; availability gates clock use. |
| [ResearchTool.cs](../integrations/rimgovernor-native/src/Bridge/ResearchTool.cs), `HomeResearchTools` | `ProgressField`, `AnomalyKnowledgeField`: readonly reflection handles. |
| [StatusTool.cs](../integrations/rimgovernor-native/src/Bridge/StatusTool.cs), `HomeStatusTools` | `ActiveAlertsField`, `LiveMessagesField`, `MessageStartingTimeField`, `DiaOptionTextField`: readonly handles; `NonBlockingWindowPrefixes`: lookup array. |
| `SupervisedPlayTool.cs`, `Supervisor` | `BoostField`: readonly handle to external mutable static; `NonStoppingLetterDefs`, `NonStoppingMessageTypes`: initialized lookup sets, no Add/Remove/Clear sites. |
| `TradeTool.cs`, `HomeTradeTools` | `LiveMessagesField`: readonly handle. |
| [ZoneCellsTool.cs](../integrations/rimgovernor-native/src/Bridge/ZoneCellsTool.cs), `HomeZoneCellTools` | `NewZoneKey`: stable identity sentinel, not a mutable zone registry. |
| `ZoneCellsTool.cs`, `StockpileFilter` | `PresetNames`: lookup array. |

## Excluded sources, fixture scope and headless effects

[ColonyObservations.csproj](../integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj)
excludes `PawnSettingsRead.cs` and `StockpileFilter.cs`, while compiling the active
classes embedded in `PawnConfigTool.cs` and `ZoneCellsTool.cs`. Do not count their
declarations as two live caches:

- [PawnSettingsRead.cs](../integrations/rimgovernor-native/src/Bridge/PawnConfigTool.cs):
  `LetterGate`, `_byLetter`, `_letterOf` duplicate the lazy process definition cache;
  `PrioritiesField`, `TrainingGetSteps`, `EggProgressField`, `SocialCacheField`,
  `SocialActiveField` are readonly reflection metadata. All eight are excluded.
- [StockpileFilter.cs](../integrations/rimgovernor-native/src/Bridge/ZoneCellsTool.cs):
  `PresetNames` is an excluded lookup array.
- `identity/**/*.cs` is excluded only from the tool assembly and compiled in its
  referenced identity assembly; all nine saved-state files were parsed, with no
  static fields. Their static methods are not storage.
- Conditionally included `scripts/fixtures/*.cs` are outside production C# integration
  sources. Only their writes to production-declared `PreparingFixture` and
  `TestSuspendUntil` are within this audit; this is not a complete fixture-state audit.

Headless files [Startup.cs](../integrations/rimgovernor-native/src/Runtime/Headless/Startup.cs),
[HeadlessModeManager.cs](../integrations/rimgovernor-native/src/Runtime/Headless/HeadlessModeManager.cs) and
[HeadlessPatches.cs](../integrations/rimgovernor-native/src/Runtime/Headless/HeadlessPatches.cs) declare no
static fields. They still change process behavior: the batch-only static constructor
writes `QualitySettings.vSyncCount` and `Application.targetFrameRate`; bootstrap and
menu-ready callbacks install Harmony patches, and long-event hooks acknowledge the
native presentation flag. No owned reset/idempotence state tracks these effects.
N01.01/05 must verify repeat menu initialization, partial patch failure, normal versus
batch startup and process-lifetime external settings. A render lease cannot reverse
batch suppression. Immutable metadata does not establish successful patching.

The semantic gaps above require isolated acceptance for cancellation, disconnect,
game/map/load change, same-value player override, shutdown, callback exceptions and
repeated initialization. This audit verified declarations, reset/use sites and links;
it did not run those native scenarios or certify lifecycle safety.

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
(`GameReuse` in `RimGovernor.Brain.Acceptance`, exercised by
the `lifecycle/reuse` acceptance case) does *not* clear: a reload into the same process leaves every
static in this table untouched. `lifecycle/reuse` proves the load-token /
authority / draft / stock reset contract only; a case asserting on one of
these statics runs in fresh-process mode.

## Coverage method

All 86 C# files under the two integration source trees were parsed with Roslyn
`CSharpSyntaxTree.ParseText`. Each `FieldDeclarationSyntax` with a `static` modifier
was expanded into its individual variables, including declarations without access
modifiers, multiple variables and `readonly` containers. Both default preprocessing
and `THROUGHPUT_FIXTURE` were inspected. Default sources contain 130 static field
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
| [PawnSettingsRead.cs](../integrations/rimgovernor-native/src/Bridge/PawnSettingsRead.cs): `LetterGate`, `_byLetter`, `_letterOf` | Process lock and lazily populated bidirectional time-assignment definition maps. `EnsureLetters` runs once; an unavailable definition database can become an empty cached mapping. | No load/map/lease reset; player schedule changes do not alter the mapping. Verify initialization after definitions are ready and repeated game reuse with the same loaded mod set. Dynamic definition replacement would require invalidation rather than stale Def references. | N01.03 observations / N01.04 pawn settings |

## Execution and safety supervision

| Owner and actual fields | Initialization, reset and lifetime | Disconnect/player behavior and stale-state hazard | N01 owner |
| --- | --- | --- | --- |
| [Supervisor](../integrations/rimgovernor-native/src/Bridge/SupervisedPlayTool.cs): `Gate`, `_state` | Process lock, one current `State`; `Start` creates epoch state capturing Game/Map, clock settings, lease, tick bounds and safety baselines. `Stop` makes it inactive but retains the object for status; later start replaces it. Failed pause keeps it active for another frame. | Frame/tick callbacks stop on expiry, identity/map change, rewind, danger or external clock change. A changed game is retired without pausing the newly loaded game. Verify shutdown/failed callbacks do not strand active authority and stale status is never read as fresh admission. | N01.05 supervision |
| `Supervisor`: `ColonyEvents` | One in-memory `ColonyEventChannel` (shared Brain.Bridge assembly): a bounded queue of 1024 plain `ColonyEvent` records and a loss counter. Written with a non-blocking `TryWrite` on the game thread; a write that finds the queue full is dropped and counted. `Open` (the brain's `OpenColonyEventsAsync`) swaps in a fresh queue, completes the replaced one and clears the counter; nothing else holds the replaced queue. No dispose API. | Never pauses the game: a loss makes the brain's next step plan Full. The queue persists across loads within the process; events carry colony/load/map identity, and the brain ignores another world's. Test overflow counting, open-empty and stale-load filtering. A brain reload reopens it, which completes the old brain's pending read, and a stopped brain's cancelled read leaves the queue at cancellation, so no waiter pins a collectible brain context (#3306). Test that an open releases the previous reader. | N01.05 events / N01.06 recovery |
| `Supervisor`: `_patched`, `_patchError` | Interlocked installation gate, error text; installation verifies update/tick Harmony owners and resets gate on exception. Process lifetime after success. | Does not periodically revalidate patch presence. Partial installation followed by retry needs idempotence/health acceptance. Missing required guard must disable capability, including after external patch changes. | N01.05 patch health |
| `Supervisor`: `InjuryStops`, `_injuryStopsSession`, `_priorEpochConsciousHostiles` | Process-held memory intentionally spans epochs. `Start` clears all three on changed Game identity; injury entries older than one hour are pruned when recording stops. Hostile count updates at probes. | Not map-scoped: switching maps in the same Game retains the prior count and injury suppression. Verify map switch, return, changed pawn set and same-Game rewind cannot suppress new danger or invent a threat-cleared transition. No explicit player reset beyond new-game baseline. | N01.05 safety observations |
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
| [DraftOwnership](../integrations/rimgovernor-native/src/Bridge/DraftOwnership.cs): `Claims` | Process `ConditionalWeakTable` keyed by exact pawn draft-controller object. `Acquire` stores owner after a draft; every patched draft setter removes the claim. No explicit bulk reset; dead keys can be collected. | Claims are unsaved; no autonomous disconnect undraft/expiry here. Brain cleanup must recheck exact ownership; a later setter, including same-value, wins. Test retained old pawn references and load/reconnect; weak-key lifetime is not a lease. | N01.04 draft writes / N01.05 cleanup |
| [OrderedWorkHistory](../integrations/rimgovernor-native/src/Bridge/OrderedWorkHistory.cs): `game`, `generations` | Process dictionary by pawn numeric ID; `Read` resets on changed Game, otherwise increments for non-owned orders/toggles. No pruning within a Game. | Player input invalidates generation. Same-Game rewind/map changes do not reset it; callers must separately bind load/map. Verify repeated game reuse and order paths not covered by `TryTakeOrderedJob`/draft gizmos. | N01.04 ownership evidence |
| `OrderedWorkHistory`: `[ThreadStatic] owned` | Per-thread nesting count incremented by `Owned`, decremented by `Scope.Dispose`. Only synchronous same-thread scope is intended. | No load reset or defensive idempotent dispose; crossing await/thread boundaries or double disposal misclassifies later player orders. Verify all callers use a single synchronous `using` and exceptions release it. | N01.04 main-thread admission |
| [NativeSnapshotVersions](../integrations/rimgovernor-native/src/Bridge/Protocol/NativeSnapshotVersions.cs): `Gate`, `Rows`, `_version`, `_reads` | Process lock, one monotonic version counter and a row per kind, colony, load, map and entity holding the last guarded field bytes. A read whose bytes differ from the row's takes the next version, so a token is `kind-<version>`. When the rows reach twice `RetainRows`, rows not read in the last `RetainRows` reads are dropped. No load reset: the identity is in the key. | A write recomputes the row's version from the live world and refuses unless it equals the token it carries; a fact that moved, even if it moved back, takes a new version, so a read before the move never matches again. An evicted row restarts at a new version, which only refuses older tokens. Versions do not survive the process. | N01.03 observations / N01.04 CAS |
| [WallUpgradeSafety](../integrations/rimgovernor-native/src/Bridge/WallUpgradeSafety.cs): `issuing` | Process bool true only around synchronous designation, reset in `finally`. | Makes the designation hook ignore the bridge's own designation. No nesting counter/thread identity: test reentrant designation and external setter inside the call. Never carry it through queued work. | N01.04 wall operations |

These classes also own process installation flags. They are **separate fields**
from their saved components or transient work scopes:

| Class and flag | Entry/reset behavior | Acceptance and family |
| --- | --- | --- |
| `DraftOwnership.patched` | `Ensure`; explicit draft-setter lookup, true after patch, no reset after success. | Setter/owner health and repeated install, N01.05. |
| `OrderedWorkHistory.installed` | `Read`; two patches, exceptions return unavailable, true only after both. | Failure after first patch and retry; unknown generation must remain unknown, N01.04/05. |
| `HomeCoverage.installed` | `Install`; Set/Clear/Invert then true, no reset. | Partial revision observation must not be treated as complete, N01.04/05. |
| `WallUpgradeSafety.installed` | `Install`; also initializes the PlayerUiRevision observer; true after all hooks. | Partial guard install and repeat initialization, N01.04/05. |
| `PlayerSpeedKeyBlock._patched`, `_inPlayerInput`, `_lastMessage` | `Ensure` installs once from the first main-thread hop; the depth counter brackets `TimeControls.DoTimeControlsGUI` (finalizer decrements); the timestamp rate-limits the in-game message. The blocking decision itself reads per-Game `NativeControlAuthority`. | Depth is not thread-local (GUI runs on the main thread only). Live-verify keys are inert while on and work after Pause, N01.05. |
| `LetterPauseHook.installed` | `EnsurePatched`; letter and three clock callbacks then true, no reset. | Partial installation/retry must preserve pause attribution, N01.05. |

These flags persist across disconnect/load/player override because Harmony patches
are process infrastructure. They must not be mistaken for per-colony authority.
Except where explicitly described, success is not rechecked against Harmony's
current patch set. N01.05 owns capability health and idempotent retry acceptance.

## Presentation and explicit player input

| Owner and actual fields | Initialization/reset/lifetime | Disconnect/player behavior and unresolved acceptance | N01 owner |
| --- | --- | --- | --- |
| [PlayerUiRevision](../integrations/rimgovernor-native/src/Bridge/PlayerUiRevision.cs): `uiRevision`, `observingUi` | Process revision counter and one-time Root.OnGUI installation flag; qualifying mouse/keyboard events increment revision. No load reset. | Player input invalidates old frame revision. Test overflow/long reuse, missing patch and same-value UI state; revision alone is insufficient without frame Game/Map checks. | N01.05 input freshness |
| [RenderDemandDriver](../integrations/rimgovernor-native/src/Bridge/RenderDemandTool.cs): `instance`, `until`, `Suspended` | Lazy persistent MonoBehaviour and map-draw patch; max-extension lease, quarter-second update. No Game/Map binding. OnDestroy clears suspension/restores cameras, but does not explicitly zero `until` or null `instance` (Unity destroyed-object semantics apply). | Visible/unknown window state renders; batch refuses. Expiry can suspend hidden-window cameras, remembered in an instance set. Restore re-enables those cameras without checking later player/other-mod changes. Test cross-load lease, object destruction/recreation, camera replacement and lost brain; no speed changes are intended. | N01.05 render lease |
| `RenderDemandDriver`: `TestSuspendUntil` | Exists only with `THROUGHPUT_FIXTURE`; process absolute real-time deadline checked in Apply. | Fixture can force suspension on Linux independently of window APIs; production default parse has no field. Verify separately identified fixture artifacts and expiry. | N01.01 fixtures / N01.05 presentation |


## Readonly metadata and lookup collections

These declared statics have no runtime mutation sites in the inspected source,
apart from initialization. They have process lifetime, no disconnect/load cleanup
and no player authority of their own. Reflection handles may still be null or
point to mutable **game-owned** data; their consumers must validate that boundary.
Arrays/sets below are mutable types despite current lookup-only use. N01.02/03/04
own typed boundary migration; N01.05 owns relevant watcher metadata health.

| Source / class | Fields and classification |
| --- | --- |
| `Protocol/NativeConstructionCausality.cs`, `ReferenceComparer<T>` | `Instance`: stateless comparer singleton; no mutable instance fields. |
| `PawnSettingsRead.cs` | `SocialCacheField`, `SocialActiveField`, `SituationalCacheField`, `SituationalDirtyField`: readonly reflected fields; observed caches belong to pawns. |
| `SupervisedPlayTool.cs`, `Supervisor` | `BoostField`: readonly handle to external mutable static; `NonStoppingLetterDefs`, `NonStoppingMessageTypes`: initialized lookup sets, no Add/Remove/Clear sites. |

## Excluded sources, fixture scope and headless effects

- `identity/**/*.cs` is excluded only from the tool assembly and compiled in its
  referenced identity assembly; all nine saved-state files were parsed, with no
  static fields. Their static methods are not storage.
- Conditionally included `scripts/fixtures/*.cs` are outside production C# integration
  sources. Only their writes to the fixture-declared
  `TestSuspendUntil` is within this audit; this is not a complete fixture-state audit.

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

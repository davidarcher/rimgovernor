# Presentation contract coverage

`presentation.proto` owns explicit player camera, selection, captured UI, input,
notification and media messages. It imports only `common.proto`; observation
composition may reuse `NotificationsSnapshot` without a dependency cycle. This is
a contract definition and source audit. Generated compilation does not establish
SDK adapter, permission, native-render or gameplay acceptance.

## Source grounding

Repository native paths below are under `integrations/rimgovernor-native/src/Bridge`.
The retained upstream SDK was inspected by ILSpy, not reconstructed from an empty
output schema: `RimBridgeServer.dll` SHA-256
`bdd0ad19036a3554eff4abeb2a5f35e4d13c0e8a4559985bdc13340589f913e0`, from the private
`n01-native-capture` inputs. Decompilation is inspection evidence only; no SDK code
is copied into the contract. Current invocation acceptance against a deployed SDK
remains separate. Historical detail inputs are in that worktree's
`.rimgovernor/live-headless-01/run/native-compatibility/*-detail.json`; the observation
audit's `native-observation-contract-evidence.json` records paths and hashes.

| Retained source surface | Typed coverage | Verified field producers and consumer |
| --- | --- | --- |
| `rimworld/get_camera_state`, `move_camera`, `set_camera_zoom` | `CameraState`, `MoveCamera`, `SetCameraZoom` | SDK `RimWorldState.DescribeCamera` produces map position, root/zoom sizes, native zoom range, normal size bounds and view rectangle. `ViewCapabilityModule` owns the operations. `dashboard_controls.py:234-289` requires finite values and extension-disabled normal zoom. |
| `get_selection_semantics`, `list_colonists`, `select_pawn`, `clear_selection` | `SelectionSnapshot`, `ColonistRoster`, exact-ID `SelectPawn`/`ClearSelection` | SDK `RimWorldSelectionSemantics.GetSelectionSemanticsResponse`, `DescribeSelectedObject`; `dashboard_controls.py:181-214`, `player_action_verification.selection_identity`. Display labels cannot identify an executable object. |
| `get_ui_state`, `get_ui_layout` | `UiState`, `UiSnapshot`, `UiSurface`, `UiElement`, `UiScroll` | SDK `UiLayoutSnapshot`, `UiLayoutSurfaceSnapshot`, `UiLayoutElementSnapshot`, `UiLayoutScrollSnapshot` declare capture/frame/time, surface/target IDs, local/screen rectangles, depth/parent, checked/disabled, scroll offsets/bounds/edges. `bridge_game.py:112-128` uses these controls. |
| `get_screen_targets` | `ScreenTargets`, `ScreenTarget`, exact captured window identity | SDK `RimWorldTargeting.CreateScreenTargetsPayload` and target resolution bind window ID/type or exact menu/option identity. The canonical target list projects executable/clip targets; it does not carry arbitrary nested SDK objects. |
| `list_main_tabs`, `list_inspect_tabs` | `TabsSnapshot`, `Tab` | SDK `RimWorldMainTabs.ToToolResponse`, `RimWorldInspectTabs.ToToolResponse`: exact IDs, native type/labels, visibility/open/hidden/disabled, def/order/window rectangle or selection fingerprint/ordinal. |
| `list_selected_gizmos` | `GizmosSnapshot`, `Gizmo` | SDK `DescribeGroupedGizmo`: selection fingerprint, group/owner counts, labels/descriptions, visibility/disabled, hotkey, right-click and reverse-designator flags. Reading a reverse designator never activates it. |
| `click_screen_target`, `click_ui_target`, `scroll_ui_target`, `open_main_tab`, `close_main_tab` | `PlayerCommand` branches with `PlayerPrecondition` | Historical input schemas verify targetId, timeoutMs and scroll deltaX/deltaY/targetX/targetY. `ScrollAxis` makes absolute versus relative adjustment explicit. Exact main-tab ID replaces label/type fuzzy matching. `bridge_runtime.py:952-990,1171-1191` consumes captures after attempted effects. |
| `home/dialog_text` | `DialogSnapshot`, `SetDialogText`, `PreviewDialogText` | `DialogTextTool.cs:62-95,188-215`: exact window ID, reviewed field, before/after, optional native maximum length. `accept=true` is refused in source and has no protocol operation. Confirmation uses a separately captured native UI action. |
| `home/confirm_colony_names` | `ConfirmColonyNames`, `NamingSnapshot`, `PreviewNaming` | `ColonyNamingTool.cs:24-78`: exact observed naming window, faction/settlement suggestions, native name validation/callbacks and closed-window readback. No generic dialog callback invocation. |
| `home/world(show=true)` | `ShowWorld`, `WorldViewResult` | `WorldTool.cs:89-153,457`: explicit tile, watch seconds, shown/wantedMode/hidden/reason. Plain world facts remain observation-owned and do not show the world view. |
| `list_letters`, `list_messages`, `list_alerts`, `home/status` notifications | `NotificationsSnapshot`, typed independently available sections | SDK `RimWorldNotifications.DescribeLetter`, `DescribeDiaOption`, `DescribeMessage`, `DescribeAlert`, `DescribeLookTargets`, `DescribeGlobalTarget`; `StatusTool.cs:109-114,457-615`. Exact IDs, native defs/types, age/timing, choices, disabled/refusal facts, complete-or-truncated targets are retained. `clock_control.py` owns event inspection/resume separately. |
| `open_letter`, `dismiss_letter` | exact `LetterTarget` branches | SDK `OpenLetterResponse`/`DismissLetterResponse`; `dialog_control.py` requires a clear prior window set and exact fresh window-removal evidence. A close does not acknowledge a stop or resume time. |
| `home/player_input` take/renew/release/event | `InputLeaseRequest`, `InputState`, `InputEvent`, acknowledgements | `PlayerInputTool.cs:20-26,238-308,338-364`; `dashboard_controls.py:35-176`, `player_input.py`, `native_input_channel.py:10-67`. Source/frame/order, held counts, lease expiry, exact captured scene and mailbox sent/received sequences remain distinct. |
| `home/render_demand` | separate `RenderState` read and `RenderDemand` lease | `RenderDemandTool.cs:15-55` reports support/suspension/window visibility/remaining lease; visible windows continue rendering. Zero means status, not stop. |
| `home/video_stream`, dashboard video transport | `VideoLeaseRequest`, `VideoState`, `MediaFrame`, exact frame acknowledgement | `VideoStreamTool.cs:16-84,153-240`, `video_stream.py:78-86,380-417`: source/sequence, size, native pixel format, capture method/time/readback, renderer/process diagnostics; exactly one outstanding frame per viewer. |
| `home/pawn_image` | `PawnImageRequest` portrait/follow, `PawnImage` | `PawnImageTool.cs:15-34,54-72,126-174`: exact colonist/current-load identity, 192x192 portrait or 640x400 independent follow, PNG bytes, observed tick. A pending capture is bounded and temporarily restores offscreen camera state. |
| `rimworld/take_screenshot` | `ScreenshotRequest`, `Screenshot` | SDK `ViewCapabilityModule.TakeScreenshot`, `CaptureScreenshotInternal`, `CreateScreenshotResponse`: optional exact clip target/padding and target metadata. Filesystem fileName/path is a transport artifact; screenshot messages are suppressed by the adapter. No arbitrary path request enters this contract. |

`rimworld/get_map_target_info`/cell facts, definition/architect catalogs and ordinary
pawn detail belong to observation contracts. Their typed facts can drive player
presentation without making those reads camera writes. Clock status/events,
supervised tick waits, save/load and process ownership are root lifecycle families.
There is no execute-gizmo/debugger/editor/zoom-extension/ultra-speed or arbitrary
native-method branch here. No automatic fallback to UI input is permitted.

## Admission, scope and uncertainty

`PlayerPresentation` is a separate authenticated explicit-player capability.
Generated service names and caller-supplied viewer/direction values do not grant
it. Deny its RPCs to advisers and automated Hands. Taking control must first
invalidate prior player direction and automation authority, enter Manual, pause
through the clock owner, release only current-load controller-owned drafts, and
verify actual native pause. `InputLeaseGranted` follows those checks. If pause,
draft cleanup or native acquisition has begun but confirmation fails, return
`InputLeaseUncertain` with the exact request, any known acquired lease and optional
last observed input state. A refusal requires proof that no admission effect began;
an incomplete handoff never acknowledges player readiness. Renew cannot
resurrect an expired lease; release/expiry/disconnect/load/map changes clear held
keys/buttons independently of controller cancellation and never resume play.

Keep the input viewer/lease distinct from simulation-write authority and clock
epoch. Player camera, selection, UI and naming commands issued through
`PlayerPresentation.Apply` (the `confirm_colony_names` branch of `PlayerCommand`)
require the same current player identity and lease plus a captured UI
precondition, exactly like every other `Apply` branch; there is no naming-specific
carve-out of that boundary. `PresentationReads.PreviewNaming` is a read of the
same authenticated surface and stays unregistered alongside it.

The initial faction/settlement dialog blocks all play, including automated
Hands, before any player lease or capture is possible, so gating its
confirmation behind `PlayerPresentation` would deadlock autopilot bootstrap.
Its autopilot-eligible path is instead `Operations.ConfirmColonyNames`
(`NativeColonyNamingOperations`, see operation-coverage.md), admitted through
the ordinary authority `WritePrecondition` every other automated write uses,
not a player lease/capture. Routine control dispatches the `ConfirmColonyNames`
goal (priority 0) through that operation. The unauthenticated legacy
`home/confirm_colony_names` tool (`ColonyNamingTool.cs:24-78`, row above)
predates it and remains for the accept-test harness and manual use; it is not
the sanctioned production dispatch path. `Apply`'s `confirm_colony_names`
branch remains reserved for a future player-facing naming review affordance
and is deliberately unregistered until one exists; it must not be relaxed to
admit automated Hands. `CaptureIdentity` binds
colony/load/map/native generation, player direction, exact complete selected IDs,
window ID/type set, and a server-retained capture. The retained native capture must
also verify camera matrices, viewport dimensions and window rectangles; those
private native comparisons cannot be replaced by a caller-generated fingerprint.
An unbound, incomplete, expired or changed capture refuses before effect.

Consume captured actionable targets on every attempted click/scroll, including
failed/uncertain dispatch; require new capture/readback before another action.
`PlayerApplied` carries current observed state, not pawn completion. `Failure`
means a proven pre-effect refusal. A timeout, transport loss or native exception
possibly after effect remains uncertain; do not convert it into a safe-to-retry
failure. `InputEventUncertain` retains the exact immutable lease/source/frame/order
request and optional last observed input state. `PlayerCommandUncertain` retains
the exact command/capture precondition and optional `PlayerObserved` readback.
These are explicit application outcomes even when the transport succeeded;
transport loss can independently create uncertainty. Last observed fields are
partial evidence, not certification of no effect or permission to retry. Relative
moves, wheel events and clicks are never replayed blindly. An acknowledgement matches exact source/frame or lease/order;
these sequences are separate from durable colony operation attempts.

Only `PreviewNaming` and `PreviewDialogText` describe the existing native dry
previews. Other UI actions have no speculative execution path. Text writes require
a reviewed naming input and its observed maximum; they do not confirm naming.
World-view cleanup may restore only its own unchanged presentation state, never a
new player choice. Native-only zoom limits are reobserved; extensions stay disabled.

## Semantic bounds and known absence

Bounds below are ordinary validator requirements, not generated Protobuf rules.
An unspecified/unknown control enum or absent required oneof/scalar refuses.
Native definition/type/kind/priority/key names remain open strings where producers
are native or mod-extensible. Validate keys against the reviewed native `Key`
whitelist; there is no arbitrary keyboard command string. Every numeric screen,
scroll, camera, age and duration value must be finite.

- Input owners/viewers/lease tokens: 1..100 characters, no NUL; source IDs 1..200.
  Other opaque IDs use the common 256 UTF-8-byte/no-NUL rule. Shared diagnostic
  text, including every uncertain detail, uses the common 4096 Unicode-scalar
  bound. Display text and native field/definition names obey the consolidated
  message-size bound; no separate guessed UTF-8 field limit is introduced. Native
  dialog text additionally obeys its observed native maximum length, retaining explicit empty text as a clear operation.
- Pointer coordinates are within the captured frame, maximum 3840x2160. Buttons
  are left/middle/right. Wheel delta is exactly -1 or +1; key code length is at most
  30 characters and must be accepted by the native whitelist. Input order starts
  at one and is strictly consecutive; source/frame sequences are nonzero and must
  not wrap. The native frame-age limit is 750 ms. Matching held releases may use
  the same-scene exception documented in `PrivatePlayerInput.Apply`; all other
  scene/camera/window/UI checks remain required.
- Preserve the current native input lease of 8 real seconds, controller viewer
  lease of 15 seconds, and one 4096-byte outstanding mailbox message with bounded
  500-ms receipt wait. An unacknowledged prior input is uncertain. No public shared
  memory path is necessary to preserve these semantics.
- Render demand lease is 1..30 real seconds (read is a separate RPC); video start
  lease is 1..15 seconds and stop is a separate branch. Pawn capture deadline is
  4 seconds. UI capture/click/scroll timeout defaults to 2000 ms; the new facade
  must enforce a maximum of 5000 ms. World view watch duration is 1..60 seconds.
- Media dimensions are 1..3840 by 1..2160. Raw frames contain exactly width*height*4
  bytes with the declared channel order/orientation and a 32-MiB body ceiling.
  Encoded frames have the same 32-MiB body cap, with dimensions verified after
  decode. The dedicated media ProtoJSON envelope has a separate 48-MiB limit,
  including base64 expansion and metadata; the 1-MiB control/observation reply
  limit does not apply to that envelope. No unbounded base64 field or fabricated
  blank image. Capture/render unavailable is explicit. Pixel reads
  and encoded viewer frames remain distinguished by `MediaEncoding`.
- Exactly one unacknowledged frame per viewer/source. Ack must match that sequence;
  old source, duplicate or out-of-order acknowledgement refuses. Existing consumer
  send/ack waits are 2/3 seconds; its 1024-byte ack cap applies before parse. Optional
  selection effect latency is 0..2000 ms; display/capture times are Unix ms, and
  readback is milliseconds. Input and frame ownership are invalidated together
  when a controlling viewer disconnects.
- Notifications default limits are 40 letters, 12 messages, 40 alerts; the
  per-section hard ceiling is 256. Existing status reads cap messages at16,
  choices at8, windows at20 and alert targets at8; upstream notifications cap
  targets at12. Preserve actual counts/omissions. Choice index is one-based in
  current producers. Unavailable or unrequested sections are distinct from an
  observed complete empty list. Inclusion defaults true; an explicit false omits
  that section, while a requested failed read has an unavailable arm. A transient
  message can expire in real time while game ticks remain paused.
- Facade ceilings: 256 windows/surfaces/tabs, 4096 elements/targets/gizmos/
  selected IDs per capture and 1 MiB per control/observation reply, measured on the
  encoded wire message. A capped read reports incomplete counts; it cannot authorize input. If full exact selection
  identity cannot fit, refuse a control capture rather than sample executable
  ownership. Snapshot reads do not promise stable pagination absent native support.

## Hard gaps before adapter completion

1. SDK output schemas are empty/prose, and `UiLayoutSurfaceSnapshot.SemanticDetails`
   is genuinely heterogeneous. Its typed variant graph has not been audited here.
   `semantic_details_unavailable` exposes that gap; no `Struct`, `Any`, object map
   or JSON string forwards it. Surface-specific semantic inspectors must use their
   reviewed observation family or add concrete variants before claiming coverage.
2. SDK selected-object detail truncates at12, and the current video correlation is
   an integer runtime hash. Neither proves complete exact selection identity. The
   new capture store must retain native references and scoped sequence identity;
   capped old payloads cannot implement `selection_complete=true`. Selection detail
   fields beyond `SelectedObject` belong to explicit observation projections;
   unreviewed extra inspect details are not silently certified complete.
3. SDK map IDs are load-ID strings while native common identity uses integer map
   IDs; some selected kinds expose specialized IDs and world targets expose
   layer-aware PlanetTile strings. Normalize from native objects with uniqueness
   checks. Do not parse labels or drop planet layers. Entry-scene UI may omit game
   context; game-affecting commands require a complete current context.
4. Current native input replies expose counts but no read-status endpoint, and
   media frames lack the full canonical capture identity. `InputStateRead`, lease
   correlation, capture/frame identity and pause/draft admission facts require
   adapter implementation and native acceptance. This contract does not claim
   those facts already exist in the SDK.
5. The ratified finite UI/media/diagnostic ceilings above require adapter
   enforcement and overflow/refusal tests. Producer defaults and caps are
   distinguished from facade requirements; no current unlimited result is silently declared
   complete. Current SDK screenshot and UI capture cannot establish atomic game
   snapshot timing; capture context must state the actual sampled tick and refuse
   stale control use.

Uncovered N01 acceptance: both graphical startup modes, rendered/unavailable/busy
capture, frame source reset and wrong/late acknowledgements, competing/expired
viewers, held-key/button cleanup, stale load/map/direction/window/selection,
unknown/disabled UI targets, review-only semantic details, exact dialog/name
readback, normal camera bounds, byte/count overflow, and no automatic access to
player mutation. No native run is claimed by this source/contract slice.

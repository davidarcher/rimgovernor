# Presentation contract

`presentation.proto` owns explicit player camera, selection, captured UI, input,
notification and media messages. It imports only `common.proto`; observation
composition may reuse `NotificationsSnapshot` without a dependency cycle.

## Scope

- Camera: `CameraState` carries map position, root/zoom sizes, native zoom range,
  normal size bounds and view rectangle; `MoveCamera`/`SetCameraZoom` require
  finite values with zoom extensions disabled.
- Selection: `SelectionSnapshot`, `ColonistRoster`, exact-ID
  `SelectPawn`/`ClearSelection`. Display labels cannot identify an executable object.
- UI: `UiState`, `UiSnapshot`, `UiSurface`, `UiElement`, `UiScroll` carry
  capture/frame/time, surface/target IDs, local/screen rectangles, depth/parent,
  checked/disabled, scroll offsets/bounds/edges. `ScreenTargets` bind window
  ID/type or exact menu/option identity and project executable/clip targets only.
- `TabsSnapshot`/`Tab`: exact IDs, native type/labels, visibility/open/hidden/
  disabled, def/order/window rectangle or selection fingerprint/ordinal.
- `GizmosSnapshot`/`Gizmo`: selection fingerprint, group/owner counts,
  labels/descriptions, visibility/disabled, hotkey, right-click and
  reverse-designator flags. Reading a reverse designator never activates it.
- `PlayerCommand` branches with `PlayerPrecondition` click, scroll and open/close
  main tabs. `ScrollAxis` makes absolute versus relative adjustment explicit.
  Exact main-tab ID replaces label/type matching.
- `DialogSnapshot`, `SetDialogText`, `PreviewDialogText`: exact window ID,
  reviewed field, before/after, optional native maximum length. There is no
  accept operation; confirmation uses a separately captured native UI action.
- `ConfirmColonyNames`, `NamingSnapshot`, `PreviewNaming`: exact observed naming
  window, faction/settlement suggestions, native name validation/callbacks and
  closed-window readback. No generic dialog callback invocation.
- `ShowWorld`, `WorldViewResult`: explicit tile, watch seconds,
  shown/wantedMode/hidden/reason. Plain world facts remain observation-owned.
- `NotificationsSnapshot` reads the letter stack, live messages and active alerts;
  each section is observed or unavailable on its own. Exact IDs (alerts are
  identified by their type), native defs/types, age/timing, choices,
  disabled/refusal facts and complete-or-truncated targets are retained.
- Exact `LetterTarget` open/dismiss branches require a clear prior window set and
  exact fresh window-removal evidence. A close does not acknowledge a stop or
  resume time.
- `ScreenshotRequest`/`Screenshot`: optional exact clip target/padding and target
  metadata. No arbitrary path request enters this contract.

Cell facts, definition/architect catalogs and ordinary pawn detail belong to
observation contracts. Their typed facts can drive player presentation without
making those reads camera writes. Clock status/events, supervised tick waits,
save/load and process ownership are root lifecycle families. There is no
execute-gizmo/debugger/editor/zoom-extension/ultra-speed or arbitrary
native-method branch here. No automatic fallback to UI input is permitted.
`UiLayoutSurfaceSnapshot` semantic details are heterogeneous;
`semantic_details_unavailable` states that no typed variant was produced, and no
`Struct`, `Any`, object map or JSON string forwards them.

## Admission, scope and uncertainty

`PlayerPresentation` is a separate authenticated explicit-player capability.
Generated service names and caller-supplied viewer/direction values do not grant
it. Deny its RPCs to advisers and automated Hands. Taking control must first
invalidate prior player direction and automation authority, enter Manual, pause
through the clock owner, (drafted pawns stay drafted, #939), and
verify actual native pause. `InputLeaseGranted` follows those checks. If pause or
native acquisition has begun but confirmation fails, return
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
Its autopilot-eligible path is instead the `NamingIntent` on Actions/Apply
(see [operations](operations.md)), applied under the ordinary native authority
every other automated write uses, not a player lease/capture. Routine control
dispatches the `ConfirmColonyNames` goal (priority 0) through that intent.
`Apply`'s `confirm_colony_names` branch remains reserved for a future
player-facing naming review affordance and is deliberately unregistered until
one exists; it must not be relaxed to admit automated Hands. `CaptureIdentity` binds
colony/load/map/native generation, player direction, exact complete selected IDs,
window ID/type set, and a server-retained capture. The retained native capture must
also verify camera matrices, viewport dimensions and window rectangles; those
private native comparisons cannot be replaced by a caller-generated fingerprint.
An unbound, incomplete, expired or changed capture refuses before effect.
Selection identity comes from native references and scoped sequence identity; a
capped or hashed selection cannot claim `selection_complete=true`. Map and world
target identities are normalized from native objects with uniqueness checks,
never parsed from labels, and planet layers are kept. Entry-scene UI may omit game
context; game-affecting commands require a complete current context. Capture
context states the actual sampled tick and refuses stale control use.

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
- The native input lease is 8 real seconds, the controller viewer lease 15
  seconds, with one 4096-byte outstanding mailbox message and a bounded 500-ms
  receipt wait. An unacknowledged prior input is uncertain.
- Render demand lease is 1..30 real seconds (read is a separate RPC).
- UI capture/click/scroll timeout defaults to 2000 ms with a maximum of 5000 ms.
  World view watch duration is 1..60 seconds.
- Media dimensions are 1..3840 by 1..2160. Raw frames contain exactly width*height*4
  bytes with the declared channel order/orientation and a 32-MiB body ceiling.
  Encoded frames have the same 32-MiB body cap, with dimensions verified after
  decode. The dedicated media ProtoJSON envelope has a separate 48-MiB limit,
  including base64 expansion and metadata. No unbounded base64 field or fabricated
  blank image. Capture/render unavailable is explicit. Pixel reads
  and encoded viewer frames remain distinguished by `MediaEncoding`.
- Notifications default limits are 40 letters, 12 messages, 40 alerts; the
  per-section hard ceiling is 256. Preserve actual counts/omissions. Choice index
  is one-based. Unavailable or unrequested sections are distinct from an
  observed complete empty list. Inclusion defaults true; an explicit false omits
  that section, while a requested failed read has an unavailable arm. A transient
  message can expire in real time while game ticks remain paused.
- Facade ceilings: 256 windows/surfaces/tabs, 4096 elements/targets/gizmos/
  selected IDs per capture. A capped read reports incomplete counts; it cannot authorize input. If full exact selection
  identity cannot fit, refuse a control capture rather than sample executable
  ownership. Snapshot reads do not promise stable pagination absent native support.

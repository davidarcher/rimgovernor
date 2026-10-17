# Presentation contract

The Presentation records (`Presentation.cs`) own the read-only camera, selection, colonist roster,
notification and render-demand messages, plus the map overlay draw request. They depend only on the
Common records; observation composition may reuse `NotificationsSnapshot` without a dependency cycle.
There is no player input family: no camera/selection/UI command, UI capture, tab, gizmo, dialog or
screenshot record exists.

## Scope

- Camera: `CameraState` carries map position, root/zoom sizes, native zoom range, normal size bounds
  and the view rectangle.
- Selection: `SelectionSnapshot` and `ColonistRoster`. Display labels cannot identify an executable
  object.
- `NotificationsSnapshot` reads the letter stack, live messages and active alerts; each section is
  observed or unavailable on its own. Exact IDs (alerts are identified by their type), native
  defs/types, age, choices and complete-or-truncated targets are retained.
- `RenderDemand`/`RenderStatus`: the render lease and whether the game window renders.
- `OverlayRequest`/`OverlayApplied`: one named map-overlay layer per call; output only.

Cell facts, definition/architect catalogs and ordinary pawn detail belong to observation contracts.
Clock status/events, supervised tick waits, save/load and process ownership are root lifecycle
families.

## Admission and uncertainty

Player presentation reads are a separate authenticated explicit-player capability. Tool names and
caller-supplied viewer/direction values do not grant it. Deny it to advisers and automated Hands.
`PlayerIdentity` claims never authenticate their own authority.

Selection identity comes from native references; map and world target identities are normalized from
native objects with uniqueness checks, never parsed from labels, and planet layers are kept. Capture
context states the actual sampled tick and refuses stale control use.

## Semantic bounds and known absence

Bounds below are ordinary validator requirements, not properties of the record types. Native
definition/type/kind names remain open strings where producers are native or mod-extensible. Every
numeric screen, camera, age and duration value must be finite.

- Viewer IDs: 1..100 characters, no NUL. Other opaque IDs use the common 256 UTF-8-byte/no-NUL rule.
  Shared diagnostic text uses the common 4096 Unicode-scalar bound.
- Render demand lease is 1..30 real seconds (read is a separate tool).
- Notifications default limits are 40 letters, 12 messages, 40 alerts; the per-section hard ceiling is
  256. Preserve actual counts/omissions. Choice index is one-based. Unavailable or unrequested
  sections are distinct from an observed complete empty list. Inclusion defaults true; an explicit
  false omits that section, while a requested failed read has an unavailable arm.

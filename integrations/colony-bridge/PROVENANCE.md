# Local research companion

The eight full C# source files in `src` were copied from
https://github.com/Snowstar38/rimworld-claude-harness/tree/89c2e90fedd51419a3db55a7f9865b0aef29b270/companion/src
for the user's explicitly requested local research integration. Original namespace,
comments and authorship context are retained. No license file was present in that
checkout; this document does not grant or imply redistribution rights.

Our project/metadata compile only observation tools: status, pawns, things,
buildings, rooms and zones, plus their shared helpers. Upstream write tools,
camera/watch delays and supervised-play policy are not compiled into this module.
RimWorld and RimBridgeServer SDK assemblies are referenced, never bundled.

Any subsequent source modifications must be recorded here and tested in-game.

Local changes:
- Extracted `PawnSettingsRead` from PawnConfigTool.cs and `StockpileFilter` from
  ZoneCellsTool.cs as shared helper classes; no exported write tools included.
- Added `thingId = pawn.GetUniqueLoadID()` to every ListPawnsTool row. Upstream
  only provided an actionable pawn ID in the optional settings block.

## Gameplay slice and dashboard integration

This iteration also compiles upstream ZoneCellsTool, PlaceBuildingTool,
PawnConfigTool, BuildingConfigTool, BillsTool, OrderTool and Watch from the same
pinned source snapshot. The extracted PawnSettingsRead and StockpileFilter files
are excluded from compilation because the full upstream modules define them.
Python validates discovered schemas, restricts the callable gameplay surface,
requires explicit dryRun for companion writes, disables watch delays, and refuses
godMode. Native receipts and post-command observations are kept separately from
claims of completed pawn work.

The React dashboard borrows the upstream overlay's game/chat/short-summary layout
and separate long-term direction, adapted to our existing dashboard styles.
No Twitch credentials, hosted service, OBS setup or inline script is included.
The temporary standalone overlay is replaced by the dashboard on port 8787.

## Persistent projects and instruments audit

- Copied upstream `TradeTool.cs` from the same pinned snapshot unchanged. Python
  keeps map-trader adjacency, disables decorative watch delays, and classifies
  list/sheet/preview/status as inspection rather than transaction execution.
- `ColonyIdentity.cs` is local code: save-backed GameComponent identity plus a
  nonserialized load token. Attaches the component when bridge extension loading
  occurs after Verse has cached component types. Only controller metadata changes.
- Python `receipts.py` copies reason/_outcome/verdict_line from upstream
  instruments/build.py; preserves explicit native placement outcomes.
- The instruments audit identifies the upstream instant gear-drop path as a
  departure from ordinary pawn labor. The model gateway rejects that operation.

- Identity persistence lives in a separate normal mod assembly
  `src/identity/ColonyIdentity.csproj` (installed under Assemblies). The bridge
  extension only exposes its read tool. This fixes Verse's cached type lookup
  during save deserialization; the live save/reload smoke verifies identity
  continuity and load-token rotation.

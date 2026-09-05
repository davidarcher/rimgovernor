# RimBot Colony Manager

One AI manages the colony through ordinary player orders. No per-pawn agents, ownership rules, difficulty patches, pawn deletion or spawned buildings. LM Studio is the default; paid APIs are explicit options.

## Play

Open Colony AI and select Automate. Reviews run when colony conditions or your direction change, respecting the configured interval and game pause. Manual stops AI and discards outstanding responses. New games start in Manual; saved Automate mode persists. Old Suggest saves load in Manual and old proposals are ignored.

The default direction is to establish and steadily develop a self-sufficient colony, including equipment, food production, cooking and useful research. Basic survival reserves are not the end goal.

The overview contains five compact status rows with details on hover, an optional direction, the current plan, and recent activity. Expand activity opens a draggable log window. There is no approval mode, separate automatic-review checkbox, or Run one review button.

Local reviews omit max_tokens, so the server/context allowance governs output. Local reviews have no turn/action cap; the optional hourly cap defaults to unlimited. Context is compacted as reviews grow. Paid adapters retain three requests/four tool actions per review and their hourly budget. A review ends when the model finishes, reports a blocker, reaches a provider/context limit, or is stopped. Daily reasoning defaults to none; strategic reasoning requests medium through a separate configurable setting. Server/model support must still be verified in play.

## Current tools and limits

- Query all visible loose items through the map item index, filtered by exact type/category/forbidden status and sorted by distance from a point. Results include stack IDs and pagination. Allow selected IDs without area scanning.
- Inspect bounded areas, colonists, pending construction, forbidden supplies, and available building materials.
- Allow items in a selected area, create a shared stockpile, set work priorities, and place individual blueprints.
- Compare nearby shelter options: reuse ruins and existing beds, enclose rock boundaries, excavate visible shallow rock, or build a new enclosure. Options expose gaps, material/mining/roof needs, distance and overhead mountain. Preparation orders mining first, then enclosure/roofing and cheap temporary sleeping spots after excavation finishes. This currently searches bounded rectangular footprints near the persistent base; it is not a general excavation/layout solver.
- Construction stays near a persistent colony base. Use returned sites rather than invented coordinates; a selected pawn/building can set a new base explicitly.
- Equip allowed weapons using normal ordered jobs; incapable colonists and equipment/reachability restrictions are respected.
- Create growing zones from discovered crops, configure production bills (target count or forever, including butchering), designate wild animals for hunting, and select available research. Normal work, skills, resources, danger and timing still apply.
- As a fallback, build a validated 7x7 room with a south doorway, zero to four beds, a clear aisle, and roof designation. It requires enough allowed materials for new orders: stone blocks or wood walls, wood beds and door. It never substitutes steel. Colonists still build everything normally. Reachability and competing material demand are not fully audited.
- Designate a roof using the actual Build Roof area. Roofs are not conduits or researched buildings.
- Save a short plan or report a visible blocker when capabilities are missing.

The model can propose broader goals in its plan, but this is not yet a complete autonomous player. Prisoner/medical room setup, combat commands, general-purpose layout planning and richer project completion checks need further tools. Starter food/research/equipment controls are implemented, but longer autonomous play still needs testing. The five measured needs are baseline status indicators, not a fixed catalog of projects.

## Strategic planning

Automate creates an initial strategy, then revisits it at world quadrum boundaries (15 days). Player direction changes also trigger planning. Finishing the current seasonal projects triggers the next development plan after the one-day interval. The strategy window also has Regenerate strategy: this bypasses scheduling cooldown, preserves the old plan until a valid replacement arrives, and does not change Manual/Automate mode. It still honors the hourly request allowance and single outstanding request. Population changes, collapse from at least two food days to below half a day, or loss of at least 25% of sheltered slots can trigger an earlier review after a one-game-day interval. Medical emergencies and active hostiles defer strategic requests to favor daily execution.

The planner makes one reasoning request with only save_strategy available. It saves a concrete next-season direction and up to eight dependency-ordered projects, annual milestones, and a fuzzy three-year direction. Each project has an ID, purpose, priority, required tools, next steps, and measurable completion conditions. The model cannot issue world orders from this request. Invalid plans leave the old strategy intact; failed requests back off for a game day and five real minutes while daily management continues.

The overview shows a compact summary; View strategy opens horizons and project details. Daily reviews receive up to three ready/in-progress projects, with fresh colony facts taking precedence. The game evaluates supported counters; unknown metrics and missing tools show explicit blockers. Completion is only as meaningful as the conditions the planner chooses: building counts alone cannot verify a fully functioning kitchen or hospital. Detailed project semantics still need richer observations and tools.

Strategy and scheduling baselines persist with the save. Progress is recalculated from current game data, including reopening goals when conditions deteriorate. Strategic local requests have a ten-minute timeout; daily local requests retain two minutes. One model request is outstanding at a time. Paid strategy requests use medium reasoning and an 8192-token output budget under the same hourly request accounting.

## Development

Run `build.ps1` to compile, run regression checks, and package `tmp/RimBot-ColonyManager.zip`. Add `-Live` for a synthetic LM Studio tool-use fixture; it does not change the game. No script automatically installs or launches RimWorld. Close the game before replacing its DLL.

`tests/GameSmoke.cs` compiles only with `-p:GameSmoke=true` and runs only with `-rimbot-selftest`. Never ship that assembly. Production code, ordinary game APIs and saved state remain in the main assembly; hot reload is not implemented.

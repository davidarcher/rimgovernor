# RimMolt and MCP comparison — 2026-09-07

**Status:** preliminary research, not the completed tool-surface audit. The player
subsequently excluded MCP adoption and requested detailed RimMolt/AutoRim coverage
and a fork assessment. The shipped RimMolt assembly is now available for local
inspection; the full comparison remains pending. Administrator investigation and
memory are implemented separately in [Administrator agency](ADMINISTRATOR_AGENCY.md).

## Findings and evidence limits

The current [RimMolt release](https://steamcommunity.com/sharedfiles/filedetails/?id=3796006886) describes an in-process MCP server covering colony observation, management, construction, combat, trade, setup and event-aware time advancement. The older July Workshop description is read-only and is not a reliable description of the September release.

The [Reddit post](https://www.reddit.com/r/singularity/comments/1w93mgg/gpt6_astra_finished_the_game_rimworld_in_15_hours/) reports a 15-hour Astra completion and links a stream playlist. We have not audited that full run, its difficulty, interventions or command receipts. It is not a controlled comparison with our local Qwen3.5-4b tribal-eight fixture.

No public RimMolt core repository or reuse license was found through the current Workshop page, web searches or the author's public GitHub repositories. Do not treat its core code as available to copy. Public companion projects exist; this does not establish that the reported run used them.

## Concrete ideas to adopt

1. **Pause ownership around long controller interruptions.** The author's [pause hook](https://github.com/leopoko/Codex-RimMolt-Pause-Hook/blob/main/hooks/pause-rimmolt.ps1) calls MCP `set_speed` with `action=pause` before compaction. Adapt the behavior to our native time API and existing pause lease; resume only a pause we own. The benchmark's speed restoration is test-only and must not become permission to override player pauses.
2. **Durable, bounded lessons.** The [reflection project](https://github.com/leopoko/Codex_Precompact_Reflection) creates a session reflection before compaction. Preserve verified command outcomes, unresolved failures, and the next supported action in our project memory. Avoid repeatedly copying full catalogs. Its implementation invokes a separate model over the transcript; that extra cost is not necessary for our routine receipt retention.
3. **MCP facade: excluded by player direction.** Keep the existing HTTP/OpenAPI transport; compare gameplay capabilities independently of transport.
4. **A direct-agent comparison mode.** Use the same game fixture, observations and native commands with one agent, bypassing departmental proposal/arbitration handoffs. Compare setup time, legal orders, completed shelter, duplicate work, model tokens and failure recovery. This separates tool usability from orchestration overhead. Keep the local 4B model as the normal target; testing another model is a separate experiment, not automatic escalation.

MCP standardizes tool discovery, calls, schema descriptions and structured results ([specification](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)). It does not automatically compress context, teach RimWorld rules, coordinate projects, or improve model reasoning. Replacing HTTP with MCP while retaining identical prompts and payloads would retain our current failure modes.

## Other source we can actually inspect

[AutoRim](https://github.com/Critical-Reynolds/autorim-mcp) is a separate project, not RimMolt. Its [build commands](https://github.com/Critical-Reynolds/autorim-mcp/blob/main/mod-src/AutoRim/Commands/BuildCommands.cs) support lines and rectangle outlines, research-filtered buildable listings, and native placement checks. Its [definition resolver](https://github.com/Critical-Reynolds/autorim-mcp/blob/main/mod-src/AutoRim/Core/DefResolver.cs) returns candidates for ambiguous names. Its [map view](https://github.com/Critical-Reynolds/autorim-mcp/blob/main/mod-src/AutoRim/Commands/MapCommands.cs) is a bounded ASCII grid with coordinates and a legend. These are useful reference patterns. We already have an enclosure compiler and native definition search; improve those instead of adding another shelter planner.

Do not copy its automatic material selection unchanged: it chooses by resource-counter quantity and falls back to a default. That does not establish safe accessible supply or economic suitability. Its placement path also deserves checks for instant definitions before reuse. The inspected LICENSE is MIT; preserve attribution if code is reused.

[M4x28's existing RIMAPI MCP server](https://github.com/M4x28/RimAPI_MCP_Server) demonstrates that MCP can sit over RIMAPI without replacing it. The inspected source uses FastMCP and manually declared endpoint models. Its MIT-licensed transport examples may be useful, but copying that entire declaration layer would duplicate our generated OpenAPI types.

RimMolt defaults to port 8787, which our dashboard already uses. Any comparison installation needs a distinct port. No third-party mod, hook or server was installed during this review; inspected source is in ignored scratch storage.

## Current playtest checkpoint

The latest stopped run allowed supplies and created a rice zone but did not complete shelter. Construction definition history was duplicated during compaction; fixed in `0b70259`. A growing specialist also issued a deconstruction-area designation outside its own crop reservation. That scope hole must be fixed before the next autonomous test. These are observed failures, not evidence that the starter-base milestone is complete.

# Typed native inspection tools

The strategist discovers a tool with `describe(name)`. The controller reads its
installed bridge schema and adds a callable `native_<namespace>__<name>` tool to
the current review. Its parameters are the native parameters directly, including
types, enums, required fields and rejection of unknown properties. The former
generic `inspect(name, arguments)` strategist entry point is removed.

Discovery returns the callable name. It does not repeat the full schema in the
tool response because the schema is now attached to the tool. Repeated discovery
does not add duplicate tools. Tools remain available until the review finishes;
there is no rotation or persistent cross-load tool registry.

Supported home-tool previews constrain `dryRun` to true in the model schema.
The existing runtime inspection gateway still enforces read-only access, and
the unmodified native schema is checked again before dispatch. Execution-only
tools return their native contract for a committed `native_operation`, with no
inspection callable. Discovery cannot expose tools outside the reviewed surface.

Architect pagination adds a controller-only `catalog_offset` parameter to the
typed catalog tool. Dispatch removes it before calling the game. Responses
include an exact typed continuation call. Cell encoding and response budgets
are applied as before; raw native receipts are not redefined.

This change applies to the strategist. Optional advisers retain their existing
read-only interfaces. Game writes still go through committed plans and Hands;
discovering a tool or successfully inspecting it does not prove work completed.

Validation covers schema preservation, duplicate discovery, preview write
refusal, execution-only tools, continuation-schema compatibility and the complete
discover/inspect/commit loop. Use `scripts/live_planner_probe.py` for model quality;
unit tests do not establish that a colony can build or survive autonomously.

Live acceptance on 2026-09-08: Qwen used typed bills and Architect reads and
committed a plan. Native construction refused its invented `Door_Wood` definition.
The run stopped after about 105 seconds at a vanilla Ancient Danger pause, with
zero orders and eight rejected planner calls. This verifies dispatch, not useful
autonomous construction. Evidence is local `.rimbot/typed-native-live.json`.

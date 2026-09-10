"""Inspect the isolated RimBridgeServer trial; never runs an LLM or RIMAPI.

Install dependencies first: pip install -e .
Use --start once, then --load-fixture to reload the disposable baseline.
Raw SDK receipts and schemas are retained under .rimgovernor/bridge/evidence.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path

from rimgovernor.bridge import BridgeError, bridge_session, gabs_executable


async def run(args):
    root = Path(args.root).resolve()
    output = root / "evidence" / time.strftime("%Y%m%d-%H%M%S")
    output.mkdir(parents=True, exist_ok=True)
    async with bridge_session(Path(args.gabs) if args.gabs else gabs_executable(root), root / "config") as bridge:
        async def record(label, awaitable):
            started = time.monotonic()
            try:
                result = await awaitable
            except BridgeError as error:
                result = error.result
                (output / f"{label}.json").write_text(result.model_dump_json(indent=2), encoding="utf8")
                raise
            (output / f"{label}.json").write_text(result.model_dump_json(indent=2), encoding="utf8")
            print(f"{label}: {time.monotonic()-started:.2f}s", flush=True)
            return result

        if args.start:
            await record("start", bridge.core("games_start", gameId=bridge.game_id))
        await record("connect", bridge.connect())
        page = await record("tools", bridge.names())
        seen = set()
        while cursor := (page.structuredContent or {}).get("nextCursor"):
            if cursor in seen:
                raise RuntimeError("Bridge discovery repeated a pagination cursor")
            seen.add(cursor)
            page = await record("tools-" + cursor, bridge.names(cursor=cursor))
        if args.load_fixture:
            await record("load", bridge.call("rimworld/load_game_ready",
                saveName="RimGovernor-tribal8-baseline", readiness="visual", timeoutMs=90000))
        paused = await record("pause", bridge.call("rimworld/set_time_speed", speed="Paused", ultraSpeedBoost=False))
        assert paused.structuredContent["paused"] is True
        colonists = []
        for name in ["get_game_info", "list_colonists", "list_architect_categories",
                     "list_zones", "list_messages", "list_letters"]:
            result = await record(name, bridge.call(f"rimworld/{name}"))
            if name == "list_colonists" and (result.structuredContent or {}).get("count") != 8:
                raise RuntimeError("Trial requires the eight-tribal fixture; colony count did not match")
            if name == "list_colonists":
                colonists = result.structuredContent["colonists"]
        for name in ["apply_architect_designator", "get_cells_info", "execute_gizmo",
                     "get_context_menu_options", "execute_context_menu_option"]:
            await record(f"schema-{name}", bridge.detail(f"rimworld/{name}"))
        designators = []
        for category in ["Furniture", "Zone", "Orders", "Structure", "Production"]:
            result = await record(f"designators-{category}", bridge.call(
                "rimworld/list_architect_designators", categoryId=category, includeHidden=False))
            designators.extend(result.structuredContent["designators"])
        await record("nearby-cells", bridge.call("rimworld/get_cells_info", x=136, z=122, width=8, height=8))
        if args.observations:
            from rimgovernor.bridge_observation import ObservationGateway, observe
            gateway = ObservationGateway(bridge)
            started = time.monotonic()
            batch = await observe(gateway)
            assert len(batch.summary.pawns) == 8
            assert len({p.thing_id for p in batch.summary.pawns}) == 8
            assert batch.summary.paused and batch.summary.same_tick
            (output / 'observation.json').write_text(batch.summary.model_dump_json(indent=2), encoding='utf8')
            (output / 'native-observation.json').write_text(json.dumps(batch.native, indent=2), encoding='utf8')
            (output / 'observation-schemas.json').write_text(json.dumps(gateway.schemas, indent=2), encoding='utf8')
            print(f'companion-observation: {time.monotonic()-started:.2f}s; '
                  f'{len(batch.summary.model_dump_json())} compact characters', flush=True)
        if args.placement_test:
            # Fixture coordinates only: production placement remains the architect's responsibility.
            spot = next(d for d in designators if d.get("buildableDefName") == "SleepingSpot")
            stock = next(d for d in designators if d.get("zoneTypeName") == "RimWorld.Zone_Stockpile")
            for label, designator, x, z, width in [("sleeping-spot", spot, 136, 122, 1), ("stockpile", stock, 139, 122, 2)]:
                await record(label+"-preview", bridge.call("rimworld/apply_architect_designator",
                    designatorId=designator["id"], x=x, z=z, width=width, height=1, dryRun=True, keepSelected=False))
                await record(label+"-apply", bridge.call("rimworld/apply_architect_designator",
                    designatorId=designator["id"], x=x, z=z, width=width, height=1, dryRun=False, keepSelected=False))
                observed = await record(label+"-observed", bridge.call("rimworld/get_cell_info", x=x, z=z))
                data = observed.structuredContent
                if label == "sleeping-spot":
                    assert any(t["defName"] == "SleepingSpot" and not t["isBlueprint"]
                               for t in data["cell"]["things"])
                else:
                    assert data["cell"]["zone"]["cellCount"] == 2
                (output / (label+"-readback.json")).write_text(json.dumps(data, indent=2))
            pawn = colonists[0]["pawnId"]
            await record("draft", bridge.call("rimworld/set_draft", pawnId=pawn, drafted=True))
            observed = await record("draft-observed", bridge.call("rimworld/list_colonists"))
            assert next(p for p in observed.structuredContent["colonists"] if p["pawnId"] == pawn)["drafted"] is True
            await record("undraft", bridge.call("rimworld/set_draft", pawnId=pawn, drafted=False))
            observed = await record("undraft-observed", bridge.call("rimworld/list_colonists"))
            assert next(p for p in observed.structuredContent["colonists"] if p["pawnId"] == pawn)["drafted"] is False
            await record("select", bridge.call("rimworld/select_pawn", pawnId=pawn, append=False))
            await record("gizmos", bridge.call("rimworld/list_selected_gizmos"))
            await record("selection", bridge.call("rimworld/get_selection_semantics"))
            await record("screenshot", bridge.call("rimworld/take_screenshot", fileName="rimgovernor-bridge-trial", includeTargets=False, suppressMessage=True))
    print(f"Evidence: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".rimgovernor/bridge")
    parser.add_argument("--gabs", help="Override the prepared profile's GABS executable")
    parser.add_argument("--start", action="store_true")
    parser.add_argument("--load-fixture", action="store_true")
    parser.add_argument("--placement-test", action="store_true")
    parser.add_argument("--observations", action="store_true")
    asyncio.run(run(parser.parse_args()))

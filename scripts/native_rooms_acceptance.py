"""Compare bounded typed rooms with native room reads in a disposable Docker game."""
from __future__ import annotations
import argparse
import asyncio
import hashlib
import json
from pathlib import Path

from native_package_acceptance import Evidence, bridge_session, check_startup_log, gabs_executable, package_files, payload, prepare, prepare_rendered
from native_compatibility_acceptance import discovery
from native_protobuf_acceptance import proto
from native_supplies_acceptance import complete


def coordinates(rows):
    return {(row["x"], row["z"]) for row in rows}


def compare_room(row, native, *, cells):
    assert row["id"] == str(native["id"])
    for field, source in (("role", "role"), ("properRoom", "properRoom"), ("doorway", "isDoorway"),
                          ("outdoors", "outdoors"), ("psychologicallyOutdoors", "psychologicallyOutdoors"),
                          ("touchesMapEdge", "touchesMapEdge"), ("fogged", "fogged"), ("openRoofCount", "openRoofCount"), ("cellCount", "cellCount")):
        assert native[source] is not None and row[field] == native[source], (row["id"], field)
    # home/list_rooms rounds Celsius to one decimal; typed facts retain the float.
    assert abs(row["temperatureC"] - native["temperature"]) <= .050001
    assert row.get("label") == native["gameLabel"]
    assert native["contentsNotListed"] == 0
    assert {item["defName"]: int(item["units"]) for item in row.get("contents", [])} == {item["defName"]: item["count"] for item in native["contents"]}
    complete(row["contentsCompleteness"], len(native["contents"]))
    assert len(row.get("beds", [])) == native["bedCount"]
    assert sorted((item["building"]["defName"], item["building"]["position"]["x"], item["building"]["position"]["z"]) for item in row.get("beds", [])) == sorted((item["defName"], item["position"]["x"], item["position"]["z"]) for item in native["beds"])
    assert coordinates([item["position"] for item in row.get("pawns", [])]) == coordinates([item["position"] for item in native["pawns"]])
    assert len(row.get("pawns", [])) == native["pawnCount"]
    assert len(row.get("stockpileZoneIds", [])) == len(native["stockpiles"])
    stats = {item["defName"].lower(): item for item in row["stats"]}
    for name, value in native["stats"].items():
        assert value is not None and "unavailable" not in stats[name]
        assert abs(stats[name]["value"] - value["value"]) <= .011
        assert stats[name]["display"] == value["display"]
    if cells:
        assert native["cellsComplete"] is True and native["cellsNotListed"] == 0
        actual = coordinates(row["cells"])
        assert actual == coordinates(native["cells"]) and len(actual) == row["cellCount"] == len(row["cells"])
        assert (row["center"]["x"], row["center"]["z"]) in actual
        assert row["extents"]["minimum"] == {"x": min(x for x,z in actual), "z": min(z for x,z in actual)}
        assert row["extents"]["maximum"] == {"x": max(x for x,z in actual), "z": max(z for x,z in actual)}
        complete(row["cellsCompleteness"], len(actual))
    else:
        assert not row.get("cells") and row["cellsCompleteness"]["page"]["complete"] is False
        assert any(issue["field"] == "cells" and issue["unavailable"]["reason"] == "UNAVAILABLE_REASON_NOT_REQUESTED" for issue in row["issues"])


async def run(root, output, *, headless=True):
    assert Path("/.dockerenv").is_file(), "Native processes run only in Docker"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "scope": "Naturally generated rooms; read-only geometry, native stats and contents, no fixture spawning or construction orders."}
    evidence = Evidence(output)
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, name, arguments=None):
                async with asyncio.timeout(180):
                    return payload(await evidence.call(bridge, label, name, arguments))

            async def wire(label, name, request):
                return proto(await call(label, "rimgovernor/" + name, {"request": json.dumps(request)}))

            try:
                async with asyncio.timeout(600):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    assert names.count("rimgovernor/observations_list_rooms") == 1
                    assert not any(name.startswith("test/") for name in names)
                    report["discovery"] = names
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    before = (await wire("identity-before", "lifecycle_read_identity", {}))["loaded"]
                    assert before["paused"] is True
                    advertised = [row for row in before["capabilities"] if row.get("fullMethodName") == "rimgovernor.observations.v1.Observations/ListRooms"]
                    assert len(advertised) == 1 and advertised[0]["support"] == "CAPABILITY_SUPPORT_SUPPORTED"
                    scope = {"scope": {"expectedIdentity": before["context"]["identity"]}}
                    legacy = await call("native-default", "home/list_rooms", {"cells": True})
                    assert legacy["success"] is True
                    native = {str(row["id"]): row for row in legacy["rooms"]}
                    default = (await wire("typed-default", "observations_list_rooms", scope))["observed"]
                    assert default["context"] == before["context"]
                    complete(default["completeness"], len(native))
                    assert {row["id"] for row in default.get("rooms", [])} == set(native)
                    for row in default.get("rooms", []): compare_room(row, native[row["id"]], cells=False)
                    candidates = [row for row in default.get("rooms", []) if row["properRoom"] and not row["outdoors"] and row["cellCount"] <= 4096]
                    assert candidates, "No naturally generated indoor room: populated acceptance requires an ordinary construction setup"
                    target = sorted(candidates, key=lambda row: (-row["cellCount"], row["id"]))[0]
                    exact = scope | {"roomIds": [target["id"]], "includeCells": True}
                    selected = (await wire("typed-exact-cells", "observations_list_rooms", exact))["observed"]
                    complete(selected["completeness"], 1)
                    compare_room(selected["rooms"][0], native[target["id"]], cells=True)
                    assert (await wire("repeat", "observations_list_rooms", exact))["observed"] == selected
                    center = selected["rooms"][0]["center"]
                    region = {"minimum": center, "maximum": center}
                    by_cell = (await wire("cell-intersection", "observations_list_rooms", exact | {"region": region}))["observed"]
                    assert by_cell["rooms"] == selected["rooms"]
                    absent = (await wire("unknown-id", "observations_list_rooms", scope | {"roomIds": ["absent-room"]}))["observed"]
                    complete(absent["completeness"], 0); assert not absent.get("rooms")
                    native_boundary = await call("native-boundary", "home/list_rooms", {"includeBoundary": True, "cells": True})
                    native_boundary = {str(row["id"]): row for row in native_boundary["rooms"]}
                    boundary = (await wire("typed-boundary", "observations_list_rooms", exact | {"includeBoundary": True}))["observed"]
                    compare_room(boundary["rooms"][0], native_boundary[target["id"]], cells=True)
                    assert sum(int(item["units"]) for item in boundary["rooms"][0].get("contents", [])) > sum(int(item["units"]) for item in target.get("contents", [])), "No populated boundary buildings tested"
                    for label, change in (("invalid-page", {"page": {"limit": 257}}), ("cursor", {"page": {"cursor": "stale"}}), ("duplicate-id", {"roomIds": [target["id"], target["id"]]}), ("unknown-write", {"set": True})):
                        refusal = await wire(label, "observations_list_rooms", scope | change)
                        assert refusal["failure"]["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    after = (await wire("identity-after", "lifecycle_read_identity", {}))["loaded"]
                    assert before["context"] == after["context"] and after["paused"] is True
                    report.update(rooms=len(native), indoor_room=target["id"], indoor_cells=target["cellCount"],
                        populated_beds=sum(len(row.get("beds", [])) for row in default.get("rooms", [])),
                        populated_pawns=sum(len(row.get("pawns", [])) for row in default.get("rooms", [])),
                        populated_stockpiles=sum(len(row.get("stockpileZoneIds", [])) for row in default.get("rooms", [])))
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report["error"] = repr(error)
        raise
    finally:
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-rooms-acceptance", headless=not args.rendered)) else 1)

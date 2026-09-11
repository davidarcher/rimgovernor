"""Check typed stock reads in a fresh production game via container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path

from native_package_acceptance import (Evidence, bridge_session, check_startup_log,
    gabs_executable, object_value, package_files, payload, prepare, prepare_rendered)
from native_protobuf_acceptance import proto
from native_compatibility_acceptance import discovery


def count(value: object) -> int:
    assert isinstance(value, str) and value.isascii() and value.isdecimal(), f"Missing/noncanonical ProtoJSON quantity: {value!r}"
    assert value == "0" or not value.startswith("0")
    number = int(value)
    assert number <= 9223372036854775807
    return number


def complete(value: dict, expected: int) -> None:
    assert value["page"]["complete"] is True and not value["page"].get("nextCursor")
    assert count(value["matched"]) == count(value["returned"]) == expected
    assert count(value["unreadable"]) == 0


def check_stock(row: dict, legacy: dict, identity: dict, *, include_held: bool) -> None:
    """Compare exact native census quantities; absence never supplies a zero."""
    assert row["definition"]["defName"] == legacy["defName"]
    mapping = {"units": "total", "stacks": "stacks", "ours": "ours", "oursUnforbidden": "oursUnforbidden",
        "forbidden": "forbidden", "otherFaction": "otherFaction", "fogged": "fogged", "reserved": "reserved",
        "inStockpile": "inStockpile", "inHomeArea": "inHomeArea"}
    if include_held:
        mapping.update(carried="carried", inContainer="inContainer", traderStock="traderStock")
    for field, source in mapping.items():
        assert type(legacy[source]) is int and legacy[source] >= 0
        assert count(row[field]) == legacy[source], f"Native stock mismatch {row['definition']['defName']}.{field}"
    held = legacy["carried"] + legacy["inContainer"] if include_held else 0
    assert count(row["spawned"]) + held == count(row["units"])
    assert count(row["playerFaction"]) <= count(row["units"])
    items = row.get("items", [])
    assert len(items) == count(row["stacks"]) and len({item["id"] for item in items}) == len(items)
    assert all(item["id"] and item["mapId"] == identity["mapId"] and "snapshot" not in item for item in items)
    complete(row["itemsCompleteness"], len(items))
    holders = row.get("holders", [])
    if include_held:
        complete(row["holdersCompleteness"], len(holders))
        assert sum(count(holder["units"]) for holder in holders) == held
        assert all(holder["holder"]["id"] and count(holder["units"]) > 0 for holder in holders)
    else:
        assert not holders and row["holdersCompleteness"]["page"]["complete"] is False
        assert all(field not in row for field in ("carried", "inContainer", "traderStock"))
        issues = {issue["field"]: issue["unavailable"]["reason"] for issue in row["issues"]}
        assert all(issues[field] == "UNAVAILABLE_REASON_NOT_REQUESTED" for field in ("carried", "in_container", "trader_stock"))
    complete(row["corpsesCompleteness"], len(row.get("corpses", [])))


async def run(root: Path, output: Path, *, headless: bool, timeout_seconds: int) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "scope": "Fresh production typed supply census against existing native stock reads; no stock spawning, fixture mutation or gameplay orders."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, name, arguments=None, timeout=30):
                async with asyncio.timeout(timeout):
                    return payload(await evidence.call(bridge, label, name, arguments))

            async def wire(label, method, arguments):
                return proto(await call(label, "rimgovernor/" + method, {"request": json.dumps(arguments)}))

            try:
                async with asyncio.timeout(timeout_seconds):
                    await evidence.record("start", {"tool": "games_start"}, bridge.core("games_start", gameId=bridge.game_id))
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    assert names.count("rimgovernor/observations_list_supplies") == 1
                    assert not any(name.startswith("test/") or "fixture" in name.lower() for name in names)
                    await call("new-game", "rimworld/start_debug_game_ready",
                        {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000}, timeout=180)
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    before = (await wire("identity-before", "lifecycle_read_identity", {}))["loaded"]
                    identity = before["context"]["identity"]
                    assert before["paused"] is True
                    for include_held in (True, False):
                        label = "held" if include_held else "spawned"
                        legacy = await call(label + "-native-census", "home/list_things", {
                            "category": "haulable", "ownership": "all", "includeHeld": include_held,
                            "maxPositionsPerDef": 0, "maxCorpsesPerRow": 0})
                        assert legacy["success"] is True
                        known = {row["defName"]: row for row in legacy["things"]}
                        assert len(known) == len(legacy["things"]), "Duplicate legacy definition rows"
                        assert all(name in known and known[name]["total"] > 0 for name in ("WoodLog", "Steel")), "Fresh native starting materials unavailable"
                        held_defs = sorted(name for name, row in known.items() if row["carried"] + row["inContainer"] > 0)
                        selected = sorted(set(["WoodLog", "Steel"] + held_defs[:14]))
                        report[label + "_held_definitions"] = held_defs
                        # No populated-held claim when the fresh colony contains none.
                        request = {"scope": {"expectedIdentity": identity}, "filter": {
                            "defNames": selected, "ownership": "all", "includeHeld": include_held}, "page": {"limit": 16}}
                        reply = await wire(label + "-typed-census", "observations_list_supplies", request)
                        assert set(reply) == {"observed"}, reply
                        observed = reply["observed"]
                        assert observed["context"] == before["context"]
                        rows = observed.get("stocks", [])
                        complete(observed["completeness"], len(selected))
                        assert {row["definition"]["defName"] for row in rows} == set(selected) and len(rows) == len(selected)
                        for row in rows:
                            check_stock(row, known[row["definition"]["defName"]], identity, include_held=include_held)
                        report[label + "_definitions_checked"] = selected
                    # Exercise declared defaults with a useful exact known-material query.
                    default = await wire("default-ownership-held", "observations_list_supplies", {
                        "scope": {"expectedIdentity": identity}, "filter": {"defNames": ["WoodLog", "Steel"]}})
                    assert set(default) == {"observed"}
                    default_rows = default["observed"].get("stocks", [])
                    complete(default["observed"]["completeness"], 2)
                    assert {row["definition"]["defName"] for row in default_rows} == {"WoodLog", "Steel"}
                    assert all(count(row["ours"]) > 0 and "carried" in row and "inContainer" in row for row in default_rows)
                    for label, request in (("bad-category", {"filter": {"category": "unknown"}}),
                                           ("bad-page", {"page": {"limit": 257}})):
                        request["scope"] = {"expectedIdentity": identity}
                        refusal = await wire(label, "observations_list_supplies", request)
                        assert set(refusal) == {"failure"} and refusal["failure"]["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    after = (await wire("identity-after", "lifecycle_read_identity", {}))["loaded"]
                    assert after["context"] == before["context"] and after["paused"] is True
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    stopped = await bridge.core("games_stop", gameId=bridge.game_id)
                    report["stop"] = stopped.model_dump(mode="json")
    except BaseException as error:
        report.update(passed=False, error=repr(error))
        raise
    finally:
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=600)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-supplies-acceptance",
        headless=not args.rendered, timeout_seconds=args.timeout_seconds)) else 1)

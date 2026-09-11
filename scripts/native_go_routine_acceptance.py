"""Live Go routine needs and Manual; launch through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import traceback

from native_building_service_acceptance import (
    Evidence, bridge_session, gabs_executable, payload, prepare, package_files,
    proto, outcome, service, poll, http_building, READS, DIAGNOSTICS, CONTROL,
    EXECUTE, add_handoff_arguments, verify_go_binary,
)
from native_go_clock_acceptance import routine_evidence


def medical_need(reply):
    colony = outcome(reply, "observed")["colonists"]
    census = colony["completeness"]
    rows = colony.get("pawns", [])
    if not census["page"]["complete"] or int(census.get("filtered", -1)) != 0 or int(census.get("unreadable", -1)) != 0:
        return "unknown"
    if len(rows) != int(census["matched"]) or len(rows) != int(census["returned"]):
        return "unknown"
    needed = False
    for pawn in rows:
        if pawn.get("dead") is True:
            continue
        values = [pawn.get("dead"), pawn.get("downed"), pawn.get("health", {}).get("bleeding"), pawn.get("health", {}).get("needsTend")]
        if any(type(value) is not bool for value in values):
            return "unknown"
        needed |= any(values)
    return "deficit" if needed else "recovered"


def audit_routine(events, baseline, capabilities, *, restart):
    rows = sorted(events, key=lambda row: row["Sequence"])
    assert rows and [r["Sequence"] for r in rows] == list(range(baseline + 1, rows[-1]["Sequence"] + 1))
    allowed = READS | DIAGNOSTICS | {"rimgovernor/observations_list_pawns"} | {"rimgovernor/clock_" + n for n in ("read_status", "read_events", "read_attempt", "pause")}
    if not restart:
        allowed |= {CONTROL, EXECUTE, "rimgovernor/operations_preview", "rimgovernor/placement_preview",
                    "rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/observations_read_colony_facts"}
    operations = {}
    for row in rows:
        if not row.get("CapabilityId"):
            assert not row.get("OperationId")
            continue
        names = capabilities.get(row["CapabilityId"], set()) & allowed
        assert len(names) == 1, f"Unexpected operation {capabilities.get(row['CapabilityId'])}"
        name = next(iter(names))
        assert operations.setdefault(row["OperationId"], name) == name
    names = list(operations.values())
    if not restart:
        assert "rimgovernor/observations_read_colony_facts" in names
    return names


async def wait_review(database):
    async with asyncio.timeout(60):
        while True:
            with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
                row = db.execute("SELECT payload FROM routine_review WHERE singleton=1").fetchone()
            if row and json.loads(row[0])["Enabled"]:
                return
            await asyncio.sleep(.05)


async def run(root, output, binary, *, go_source, go_sha256):
    assert Path("/.dockerenv").is_file(), "Use the isolated scenario launcher"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source,
              "scope": "Native core/emergency facts reach fourteen durable Go needs; Manual invalidates them; disabled restart neither acquires authority nor reads routine facts. No routine method execution claim."}
    evidence = Evidence(output)
    launched = False
    try:
        configuration = prepare(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        gabs, profile = Path(gabs_executable(root, configuration)), root / "headless-profile"
        private, database = output / "rimgovernor-go", output / "service.sqlite"
        shutil.copyfile(binary, private)
        private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)

        async def wire(bridge, label, name, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + name, {"request": json.dumps(request)})))

        async def capture(bridge, label, baseline=None, restart=False):
            catalog = payload(await evidence.call(bridge, label + "-capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] and not catalog["truncated"]
            caps = {r["id"]: set(r["aliases"]) for r in catalog["capabilities"]}
            request = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None:
                request["afterSequence"] = baseline
            rows = payload(await evidence.call(bridge, label + "-events", "rimbridge/list_operation_events", request))["events"]
            assert rows and len(rows) < 5000
            if baseline is not None:
                report.setdefault("traces", {})[label] = audit_routine(rows, baseline, caps, restart=restart)
            return max(r["Sequence"] for r in rows)

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id)
            launched = True
            await bridge.connect()
            await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            initial = outcome(await wire(bridge, "before", "lifecycle_read_identity", {}), "loaded")
            identity, tick = initial["context"]["identity"], int(initial["context"]["tick"])
            assert initial["paused"]
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 1}))
            assert prepared["success"] and all(prepared[k] == identity[k] for k in identity)
            report["prepared"] = prepared
            report["initial_colony"] = await wire(bridge, "initial-colony", "observations_read_status", {
                "scope": {"expectedIdentity": identity}, "colonists": True, "threats": True, "colonistDetail": False, "page": {"limit": 256}})
            baseline = await capture(bridge, "setup")

        async with service(private, gabs, configuration, profile, database, output / "operate", report, clock_control=True, routine_reviews=True) as http:
            await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True))
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            submission = await http("POST", "/api/buildings/plans", body={"requestId": "routine-context", "expected": identity,
                "building": http_building(prepared["sites"][0])}, expected=201)
            granted = await http("POST", "/api/player/control/acquire", body={"requestId": "routine-acquire", "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": "0"})
            assert granted["record"]["phase"] == "granted"
            await wait_review(database)
            active = routine_evidence(database, identity, enabled=True)
            assert active["review"]["Tick"] == tick, "Review did not use the initial paused boundary"
            assert active["goals"]["CriticalMedical"]["Need"] == medical_need(report["initial_colony"])
            report["active_routine"] = active
            manual = await http("POST", "/api/player/control/manual", body={"requestId": "routine-manual", "expected": identity})
            assert manual["record"]["phase"] == "disabled" and not manual["state"]["enabled"]
            report["manual_routine"] = routine_evidence(database, identity, enabled=False)

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "operate", baseline)
            final = outcome(await wire(bridge, "after-manual", "lifecycle_read_identity", {}), "loaded")
            assert final["paused"] and final["context"]["identity"] == identity
            baseline = await capture(bridge, "restart-baseline")
        async with service(private, gabs, configuration, profile, database, output / "restart", report, clock_control=True, routine_reviews=True) as http:
            await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True))
            await asyncio.sleep(2)
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            assert routine_evidence(database, identity, enabled=False) == report["manual_routine"]
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "restart", baseline, restart=True)
            after = outcome(await wire(bridge, "final", "lifecycle_read_identity", {}), "loaded")
            assert after["paused"] and after["context"]["tick"] == final["context"]["tick"]
        report["passed"] = True
    except BaseException as error:
        report.update(error=repr(error), traceback=traceback.format_exc())
    finally:
        if launched and all(p.get("joined") for p in report.get("service_phases", [])):
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report.update(passed=False, cleanup_error=repr(error))
        elif launched:
            report.update(passed=False, resources_retained=True)
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-binary", type=Path, required=True)
    add_handoff_arguments(parser)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-routine-acceptance", args.go_binary,
        go_source=args.go_source, go_sha256=args.go_sha256)) else 1)

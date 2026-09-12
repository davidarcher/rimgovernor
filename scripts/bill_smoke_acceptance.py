"""Live-game proof for the Go billsmoke command: real native AddBill dispatch.

Launches the unified native package in a real, owned RimWorld process, creates a
disposable bench via the routine production fixture, and runs billsmoke's
"add" mode against it with a real fixture bill distinct from the bench's
existing bill. This is the acceptance counterpart to
native_guarded_construction_acceptance.py, scoped to proving the new Go
bill-dispatch scenario pattern end-to-end; it does not simulate bill
completion.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path

from rimgovernor.bridge import bridge_session, gabs_executable
from rimgovernor.headless import prepare, prepare_rendered

from native_compatibility_acceptance import Evidence, object_value, payload
from native_package_acceptance import check_startup_log, package_files


def protobuf_outcome(result, case: str) -> dict:
    assert not result.isError, "SDK refused Protobuf request"
    encoded = payload(result).get("payload")
    assert isinstance(encoded, str) and len(encoded.encode("utf8")) <= 1024 * 1024
    message = object_value(json.loads(encoded), "ProtoJSON reply")
    assert set(message) == {case}, f"Expected {case} reply, received {sorted(message)}"
    return object_value(message[case], case)


async def go_add(binary: Path, gabs: Path, configuration: Path, profile: Path,
                  output: Path, request: Path, report: dict) -> dict:
    destination = output / "go-add"
    record = {"mode": "add", "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest()}
    report.setdefault("go", []).append(record)
    command = [str(binary.resolve()), "-mode", "add", "-gabs", str(gabs.resolve()),
        "-config", str(configuration.resolve()), "-profile", str(profile.resolve()),
        "-state", str((output / "bill.sqlite").resolve()), "-output", str(destination.resolve()),
        "-game", "rimgovernor-trial", "-force-takeover", "-execute", "-request", str(request.resolve())]
    with (output / "go-add-stdout.txt").open("wb") as stdout, (output / "go-add-stderr.txt").open("wb") as stderr:
        process = await asyncio.create_subprocess_exec(*command, stdout=stdout, stderr=stderr)
        try:
            async with asyncio.timeout(150):
                await process.wait()
        finally:
            if process.returncode is None:
                process.kill()
                await process.wait()
            record["exit_code"] = process.returncode
    assert process.returncode == 0, f"Go add failed; retained report and stdout/stderr under {destination}"
    result = object_value(json.loads((destination / "report.json").read_text()), "Go report")
    assert result.get("passed") is True, result
    assert result.get("nativeCalled") is True, result
    record["report"] = result
    return result


async def run(root: Path, output: Path, binary: Path, *, headless: bool, timeout_seconds: int) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report: dict = {"passed": False, "headless": headless,
        "scope": "Disposable routine-production fixture bench, one real native AddBill "
                 "dispatch through the new Go billsmoke command. No bill completion "
                 "or observe-mode reconciliation is exercised here."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        config = object_value(json.loads((configuration / "config.json").read_text(encoding="utf8")), "configuration")
        game = object_value(object_value(config["games"], "games")["rimgovernor-trial"], "game")
        report["package_files"] = package_files(Path(str(game["workingDir"])))
        profile = root / ("headless-profile" if headless else "profile")
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            try:
                async with asyncio.timeout(timeout_seconds):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready",
                        {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    loaded = protobuf_outcome(await evidence.call(bridge, "identity", "rimgovernor/lifecycle_read_identity",
                        {"request": "{}"}), "loaded")
                    identity = object_value(object_value(loaded["context"], "identity context")["identity"], "identity")
                    report["identity"] = identity
                    prepared = payload(await evidence.call(bridge, "prepare", "test/routine_production_prepare", {}))
                    assert prepared.get("success") is True, prepared
                    report["prepared"] = prepared
                    bench_id = prepared["bench"]
                    facts = protobuf_outcome(await evidence.call(bridge, "colony-facts", "rimgovernor/observations_read_colony_facts",
                        {"request": json.dumps({"scope": {"expectedIdentity": identity}})}), "observed")
                    cooking = [row for row in facts.get("cooking", []) if object_value(row, "cooking row")
                               .get("bench", {}).get("id") == bench_id]
                    assert len(cooking) == 1, f"Expected exactly one cooking bench matching fixture id {bench_id}"
                    bench = cooking[0]["bench"]
                    token = bench["snapshot"]["token"]
                    assert isinstance(token, str) and token
                    existing = {row.get("recipe", {}).get("defName") for row in cooking[0].get("bills", [])}
                    candidates = [row for row in cooking[0].get("recipes", [])
                                  if row.get("availableOnBench") is True
                                  and row.get("recipe", {}).get("defName") not in existing]
                    assert candidates, f"No available recipe distinct from the fixture's existing bills: {existing}"
                    recipe = sorted(candidates, key=lambda row: row["recipe"]["defName"])[0]["recipe"]["defName"]
                    report["selected_recipe"] = recipe
                    fixture_request = output / "bill-request.json"
                    fixture_request.write_text(json.dumps({
                        "bench": bench_id, "recipe": recipe, "token": token,
                        "mode": "food_target", "target": 5,
                    }), encoding="utf8")
                    added = await go_add(binary, gabs_executable(root, configuration), configuration, profile,
                                          output, fixture_request, report)
                    await evidence.record("reconnect", {"tool": "games_connect", "forceTakeover": True},
                        bridge.core("games_connect", gameId=bridge.game_id, forceTakeover=True))
                    lookup = protobuf_outcome(await evidence.call(bridge, "recheck-colony-facts",
                        "rimgovernor/observations_read_colony_facts",
                        {"request": json.dumps({"scope": {"expectedIdentity": identity}})}), "observed")
                    recheck = [row for row in lookup.get("cooking", []) if row.get("bench", {}).get("id") == bench_id][0]
                    assert any(row.get("recipe", {}).get("defName") == recipe for row in recheck.get("bills", [])), \
                        "Native bench does not show the bill billsmoke reported as accepted"
                    report["confirmed_bill_present"] = True
                    log = root / ("HeadlessPlayer.log" if headless else "Player.log")
                    check_startup_log(log.read_text(encoding="utf8", errors="replace"), headless=headless)
                    report["passed"] = True
            except BaseException as error:
                report["error"] = repr(error)
                raise
            finally:
                try:
                    async with asyncio.timeout(60):
                        stopped = await bridge.core("games_stop", gameId=bridge.game_id)
                        report["stop"] = stopped.model_dump(mode="json")
                except BaseException as cleanup_error:
                    report["cleanup_error"] = repr(cleanup_error)
                    report["passed"] = False
                    if "error" not in report:
                        raise
    except BaseException as error:
        report["passed"] = False
        report.setdefault("error", repr(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return bool(report["passed"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--billsmoke", type=Path, required=True)
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=600)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "bill-smoke-acceptance",
        args.billsmoke, headless=not args.rendered, timeout_seconds=args.timeout_seconds)) else 1)

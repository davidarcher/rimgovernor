"""Exercise the production Protobuf read adapters in a fresh disposable game.

Launch with container_scenario.py. Native read assertions complement official
C#/Go parser tests; this script does not define a second wire decoder.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import hashlib
import shutil
from pathlib import Path

from native_package_acceptance import (
    Evidence, bridge_session, check_startup_log, gabs_executable,
    object_value, package_files, payload, prepare, prepare_rendered,
)
from native_compatibility_acceptance import discovery

PREFIX = "rimgovernor/"


def proto(envelope: dict[str, object]) -> dict[str, object]:
    value = envelope.get("payload")
    assert isinstance(value, str), "SDK reply did not preserve the ProtoJSON string envelope"
    assert len(value.encode("utf8")) <= 1024 * 1024
    return object_value(json.loads(value), "ProtoJSON reply")


def unchanged(before: dict[str, object], after: dict[str, object], label: str) -> None:
    assert {k: v for k, v in before.items() if k != "operation"} == {
        k: v for k, v in after.items() if k != "operation"
    }, f"Read adapters changed {label}"


async def run_go_preview(binary: Path, root: Path, output: Path, configuration: Path,
                         fixture: dict[str, object], report: dict[str, object]) -> None:
    private = root / "go-preview-smoke"
    private.mkdir(exist_ok=False)
    executable = private / "previewsmoke-linux-amd64"
    shutil.copyfile(binary, executable)
    executable.chmod(0o700)
    requests = output / "go-preview-requests.json"
    requests.write_text(json.dumps(fixture, ensure_ascii=False), encoding="utf8")
    process_report: dict[str, object] = {"binary_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
        "requests_sha256": hashlib.sha256(requests.read_bytes()).hexdigest()}
    report["go_preview_smoke"] = process_report
    stdout_path, stderr_path = output / "go-preview-stdout.txt", output / "go-preview-stderr.txt"
    with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
        process = await asyncio.create_subprocess_exec(str(executable.resolve()),
            "-gabs", str(Path(gabs_executable(root, configuration)).resolve()),
            "-config", str(configuration.resolve()), "-game", "rimgovernor-trial",
            "-force-takeover",
            "-requests", str(requests.resolve()), "-output", str((output / "go-preview-results").resolve()),
            stdout=stdout, stderr=stderr)
        try:
            async with asyncio.timeout(120):
                await process.wait()
        except BaseException:
            if process.returncode is None:
                process.kill()
            await process.wait()
            raise
        finally:
            process_report.update(exit_code=process.returncode,
                stdout=stdout_path.name, stderr=stderr_path.name)
    assert process.returncode == 0, "Go preview smoke failed; see retained stdout/stderr and artifacts"



async def run(root: Path, output: Path, *, headless: bool, timeout_seconds: int, go_preview_smoke: Path | None = None, routine_production: bool = False, routine_naming: bool = False) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report: dict[str, object] = {"passed": False, "headless": headless,
        "scope": "Official Protobuf package loading, identity/status/placement reads, malformed request refusal and paused game invariance. No pawn-work claim."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        config = object_value(json.loads((configuration / "config.json").read_text(encoding="utf8")), "configuration")
        game = object_value(object_value(config["games"], "games")["rimgovernor-trial"], "game")
        report["package_files"] = package_files(Path(str(game["workingDir"])))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label: str, name: str, arguments: dict[str, object] | None = None,
                           *, startup: bool = False) -> dict[str, object]:
                async with asyncio.timeout(180 if startup else 30):
                    return payload(await evidence.call(bridge, label, name, arguments))

            async def wire(label: str, method: str, request: dict[str, object]) -> dict[str, object]:
                return proto(await call(label, PREFIX + method, {"request": json.dumps(request)}))

            try:
                async with asyncio.timeout(timeout_seconds):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    expected = {PREFIX + name for name in ("lifecycle_read_identity", "authority_read_status", "placement_preview")}
                    assert expected <= set(names), f"Missing Protobuf tools: {expected - set(names)}"
                    assert "home/placement_previews" not in names, "Obsolete placement alias is still exported"
                    fixture_names = {name for name in names if name.startswith("test/") or "fixture" in name.casefold()}
                    assert fixture_names == (({"test/routine_production_prepare"} if routine_production else set()) | ({"test/modal_fixture"} if routine_naming else set()))
                    report["protobuf_tools"] = sorted(expected)
                    await call("new-game", "rimworld/start_debug_game_ready",
                        {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000}, startup=True)
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    from native_colony_facts_checks import prepare_food_stock
                    report['food_setup'] = await prepare_food_stock(call)
                    if routine_production:
                        report['production_setup'] = await call('production-setup', 'test/routine_production_prepare', {})
                        assert report['production_setup']['success'] and report['production_setup']['foodCreated'] == 0
                    identity_before = await wire("identity-before", "lifecycle_read_identity", {})
                    loaded = object_value(identity_before.get("loaded"), "loaded identity")
                    context = object_value(loaded.get("context"), "context")
                    identity = object_value(context.get("identity"), "identity")
                    assert loaded.get("paused") is True and identity.get("colonyId") and identity.get("loadToken")
                    assert "mapId" in identity and int(str(context["tick"])) >= 0
                    authority = await wire("authority", "authority_read_status", {"identity": identity})
                    report["authority"] = authority
                    camera = await call("camera-before", "rimworld/get_camera_state")
                    position = object_value(camera["mapPosition"], "map position")
                    x, z = int(str(position["x"])), int(str(position["z"]))
                    building_args = {"x": x, "z": z, "radius": 4, "category": "all", "aggregate": False}
                    buildings = await call("buildings-before", "home/list_buildings", building_args)
                    status_args = {"colonists": False, "threats": False}
                    status = await call("status-before", "home/status", status_args)
                    candidates = [dict(defName=definition, x=x, z=z, rotation="ROTATION_NORTH", stuff=stuff)
                        for definition, stuff in (("SleepingSpot", ""), ("Wall", ""),
                            ("RimGovernor_MissingPreviewDefinition", ""), ("Steel", ""),
                            ("Wall", "ComponentIndustrial"), ("Bed", "BlocksGranite"), ("SleepingSpot", "WoodLog"))]
                    request: dict[str, object] = {"identity": identity, "placements": candidates}
                    preview = await wire("controls", "placement_preview", request)
                    batch = object_value(preview.get("batch"), "placement batch")
                    assert batch.get("context") == context
                    results = batch.get("results")
                    assert isinstance(results, list) and len(results) == len(candidates)
                    assert ["evaluated" in row for row in results] == [True, True, False, False, False, True, False]
                    for row in results:
                        if "evaluated" in row:
                            evaluated = object_value(row["evaluated"], "evaluated")
                            assert type(evaluated.get("canPlace")) is bool
                            assert evaluated.get("rotations")
                            materials = object_value(evaluated.get("materials"), "materials")
                            assert ("known" in materials) != ("unavailable" in materials)
                        else:
                            assert object_value(row.get("failure"), "failure").get("code")
                    cases: list[tuple[str, dict[str, object]]] = [
                        ("missing", {}), ("null", {"request": None}), ("object", {"request": request}),
                        ("array", {"request": []}), ("number", {"request": 42}),
                        ("boolean", {"request": True}), ("malformed", {"request": "{"}),
                        ("unknown-outer", {"request": json.dumps(request), "unexpected": True}),
                        ("unknown-inner", {"request": json.dumps(dict(request, unexpected=True))}),
                        ("empty", {"request": "{}"}),
                        ("unknown-rotation", {"request": json.dumps(dict(request,
                            placements=[dict(candidates[0], rotation=999)]))}),
                        ("missing-coordinate", {"request": json.dumps(dict(request,
                            placements=[{k: v for k, v in candidates[0].items() if k != "x"}]))}),
                    ]
                    for label, arguments in cases:
                        refused = proto(await call(label, PREFIX + "placement_preview", arguments))
                        assert object_value(refused.get("failure"), "failure").get("code") == "FAILURE_CODE_INVALID_REQUEST", label
                    stale = await wire("stale-identity", "placement_preview",
                        dict(request, identity=dict(identity, loadToken="stale-load")))
                    assert object_value(stale.get("failure"), "failure").get("code") == "FAILURE_CODE_STALE_IDENTITY"
                    authority_status = object_value(authority.get("status"), "authority status")
                    assert ("unavailable" in authority_status) != ("inactive" in authority_status)
                    assert "active" not in authority_status
                    from native_colony_facts_checks import verify_colony_facts
                    report['colony_facts'] = await verify_colony_facts(wire, call, identity, context)
                    if routine_production:
                        assert report['colony_facts']['farms_compared'] > 0 and report['colony_facts']['cooking_compared'] > 0
                        assert report['colony_facts']['cooking_ready'] is True
                    if go_preview_smoke is not None:
                        chosen = candidates[0] if results[0].get("evaluated", {}).get("canPlace") is True else None
                        if chosen is None:
                            nearby = [dict(candidates[0], x=x+dx, z=z+dz)
                                for dx in range(-4, 4) for dz in range(-4, 4) if x+dx >= 0 and z+dz >= 0]
                            for offset in range(0, len(nearby), 16):
                                options = nearby[offset:offset+16]
                                searched = await wire(f"site-search-{offset}", "placement_preview",
                                    {"identity": identity, "placements": options})
                                found = object_value(searched.get("batch"), "search batch")["results"]
                                chosen = next((candidate for candidate, row in zip(options, found, strict=True)
                                    if row.get("evaluated", {}).get("canPlace") is True), None)
                                if chosen is not None:
                                    break
                        assert chosen is not None, "No placeable SleepingSpot found in bounded search"
                        selected = [chosen, dict(chosen, defName="Wall", x=0, z=0),
                            dict(chosen, defName="RimGovernor_MissingPreviewDefinition")]
                        verified = await wire("go-controls", "placement_preview", {"identity": identity, "placements": selected})
                        go_rows = object_value(verified.get("batch"), "Go control batch")["results"]
                        assert go_rows[0].get("evaluated", {}).get("canPlace") is True
                        assert go_rows[1].get("evaluated", {}).get("canPlace") is False
                        assert "failure" in go_rows[2]
                        fixture = {"version": 1, "placements": selected,
                            "expected": ["placeable", "refused", "invalid_definition"]}
                        try:
                            await run_go_preview(go_preview_smoke, root, output, configuration, fixture, report)
                        finally:
                            async with asyncio.timeout(30):
                                await bridge.core("games_connect", gameId=bridge.game_id, forceTakeover=True)
                    unchanged(identity_before, await wire("identity-after", "lifecycle_read_identity", {}), "identity/tick/generation")
                    unchanged(camera, await call("camera-after", "rimworld/get_camera_state"), "camera")
                    unchanged(buildings, await call("buildings-after", "home/list_buildings", building_args), "buildings/blueprints/frames")
                    unchanged(status, await call("status-after", "home/status", status_args), "paused native status")
                    if routine_naming:
                        from native_routine_naming_checks import verify_naming
                        report['naming'] = await verify_naming(wire, call, identity, output)
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(
                        encoding="utf8", errors="replace"), headless=headless)
                    report.update(passed=True, structural_refusals=len(cases), candidates=len(candidates), context=context)
            finally:
                try:
                    async with asyncio.timeout(60):
                        await bridge.core("games_stop", gameId=bridge.game_id)
                except BaseException as error:
                    report.update(passed=False, cleanup_error=repr(error))
                    raise
    except BaseException as error:
        report.update(passed=False, error=repr(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return bool(report["passed"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=600)
    parser.add_argument("--go-preview-smoke", type=Path)
    parser.add_argument("--routine-production", action="store_true")
    parser.add_argument("--routine-naming", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-protobuf-acceptance",
        headless=not args.rendered, timeout_seconds=args.timeout_seconds, go_preview_smoke=args.go_preview_smoke, routine_production=args.routine_production, routine_naming=args.routine_naming)) else 1)

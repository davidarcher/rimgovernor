"""Explicit Go HTTP building service acceptance; run only through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
from contextlib import asynccontextmanager
import hashlib
import json
from pathlib import Path
import shutil
from urllib.parse import urlsplit

import httpx

from native_go_service_acceptance import Evidence, bridge_session, gabs_executable, payload, prepare, prepare_rendered
from native_guarded_construction_acceptance import ScenarioClock, outcome
from native_protobuf_acceptance import proto
from native_package_acceptance import package_files
from native_compatibility_acceptance import discovery, validate_discovery
from rimgovernor.native_scenario import advance_game

READS = {"rimgovernor/" + name for name in ("lifecycle_read_identity", "observations_read_status",
    "observations_get_cells", "observations_list_buildings", "authority_read_status", "receipts_lookup", "receipts_observe_progress")}
DIAGNOSTICS = {"rimbridge/list_operation_events", "rimbridge/list_capabilities"}
EXECUTE = "rimgovernor/operations_execute"
CONTROL = "rimgovernor/authority_control"


def http_building(site: dict) -> dict:
    rotations = {"ROTATION_" + value.upper(): value for value in ("north", "east", "south", "west")}
    assert site["rotation"] in rotations, "Unsupported fixture rotation"
    return {key: site[key] for key in ("defName", "x", "z", "stuff")} | {"rotation": rotations[site["rotation"]]}


def audit(events: list[dict], baseline: int, capabilities: dict[str, set[str]], phase: str) -> list[str]:
    """Count attributed operations only after checking the full diagnostic sequence."""
    ordered = sorted(events, key=lambda row: row["Sequence"])
    sequences = [row["Sequence"] for row in ordered]
    assert sequences and sequences == list(range(baseline + 1, sequences[-1] + 1)), "SDK event sequence is incomplete or duplicated"
    allowed = READS | DIAGNOSTICS
    if phase == "execute":
        allowed |= {CONTROL, EXECUTE, "rimgovernor/operations_preview", "rimgovernor/placement_preview"}
    elif phase == "orchestrator":
        allowed |= {"home/supervised_play", "home/status", "home/colony_identity", "rimworld/open_letter",
            "rimworld/dismiss_letter", "rimworld/set_time_speed"}
    else:
        assert phase in {"submit", "restart"}
    operations = {}
    for row in ordered:
        capability = row.get("CapabilityId")
        if not capability:
            assert not row.get("OperationId"), "Unattributed SDK operation"
            continue
        selected = capabilities.get(capability, set()) & allowed
        assert len(selected) == 1, f"Unapproved/ambiguous capability in {phase}: {capability}: {capabilities.get(capability)}"
        name = next(iter(selected))
        identifier = row["OperationId"]
        assert operations.setdefault(identifier, name) == name, "Operation capability changed"
    names = list(operations.values())
    if phase == "execute":
        assert names.count(EXECUTE) == 1 and names.count(CONTROL) >= 2, "Need one placement and explicit acquire/Manual controls"
        assert names.index(CONTROL) < names.index(EXECUTE) < len(names) - 1 - names[::-1].index(CONTROL)
    if phase == "restart":
        assert "rimgovernor/receipts_lookup" in names and "rimgovernor/receipts_observe_progress" in names, "Restart did not reconcile native evidence"
    assert "rimgovernor/lifecycle_read_identity" in names, "Missing native identity observations"
    return names


def action(plan: dict, submission: dict) -> dict:
    assert plan["id"] == submission["planId"] and plan["revision"] == submission["revision"]
    assert len(plan["actions"]) == 1
    value = plan["actions"][0]
    assert value["id"] == submission["actionId"] and value["building"] == submission["building"]
    return value["progress"]


def verify_progress(plan: dict, submission: dict, completed: bool) -> None:
    progress = action(plan, submission)
    assert progress["attempt"] == "1" and progress["receipt"] == "accepted"
    assert progress["stage"] == ("completed" if completed else "awaiting_observation")
    assert progress["unresolved"] is (not completed)
    if completed:
        assert progress["effect"] == "completed"


def canonical_handoff_hex(value: str, length: int) -> str:
    if len(value) != length or any(char not in "0123456789abcdef" for char in value):
        raise ValueError(f"Handoff must be exactly {length} lowercase hexadecimal characters")
    return value


def validate_go_handoff(go_source: str, go_sha256: str) -> None:
    canonical_handoff_hex(go_source, 40)
    canonical_handoff_hex(go_sha256, 64)


def add_handoff_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--go-source", required=True, type=lambda value: canonical_handoff_hex(value, 40))
    parser.add_argument("--go-sha256", required=True, type=lambda value: canonical_handoff_hex(value, 64))


def verify_go_binary(binary: Path, *, go_source: str, go_sha256: str) -> str:
    validate_go_handoff(go_source, go_sha256)
    actual = hashlib.sha256(binary.read_bytes()).hexdigest()
    if actual != go_sha256:
        raise ValueError("Go service differs from supplied immutable handoff")
    return actual


def redact_http_value(path: str, value: dict) -> dict:
    recorded = dict(value)
    if path == "/api/player/session" and "token" in recorded:
        recorded["token"] = "[redacted]"
    return recorded


def service_argv(binary, gabs, configuration, profile, state):
    return [str(binary.resolve()), "serve", "--player-control", "--profile", str(profile.resolve()),
        "--gabs", str(gabs.resolve()), "--config", str(configuration.resolve()), "--game", "rimgovernor-trial",
        "--state", str(state.resolve()), "--listen", "127.0.0.1:0", "--refresh", "1s", "--timeout", "15s"]


class DrainIncomplete(RuntimeError):
    pass


async def joined_shutdown(process, record, timeout=30):
    if process.returncode is None:
        process.terminate()  # SIGTERM to this owned child PID only; no kill fallback.
    try:
        async with asyncio.timeout(timeout):
            await process.wait()
    except TimeoutError as error:
        record.update(joined=False, resources_retained=True)
        raise DrainIncomplete("Owned service did not join; no GABS attachment or game cleanup is permitted") from error
    record.update(joined=True, exit_code=process.returncode)
    assert process.returncode == 0, "Owned Go service exited unsuccessfully"


@asynccontextmanager
async def service(binary, gabs, configuration, profile, state, directory, report, *, clock_control=False, routine_reviews=False):
    directory.mkdir()
    record = {"phase": directory.name, "argv": service_argv(binary, gabs, configuration, profile, state), "joined": False}
    if clock_control:
        record["argv"].append("--clock-control")
    if routine_reviews:
        assert clock_control, "Routine review acceptance requires clock control"
        record["argv"].append("--routine-reviews")
    report.setdefault("service_phases", []).append(record)
    with (directory / "stderr.txt").open("wb") as stderr, (directory / "stdout.txt").open("wb") as stdout:
        process = await asyncio.create_subprocess_exec(*record["argv"], stdout=asyncio.subprocess.PIPE, stderr=stderr)
        record["pid"] = process.pid
        reader = None
        try:
            async with asyncio.timeout(120):
                line = await process.stdout.readline()
            stdout.write(line); stdout.flush()
            prefix = "RimGovernor Go player service: "
            assert line.decode().startswith(prefix), "Missing player-service address"
            url = line.decode().strip()[len(prefix):]
            parsed = urlsplit(url)
            assert parsed.scheme == "http" and parsed.hostname == "127.0.0.1" and parsed.port and not parsed.username
            record["url"] = url

            async def drain_stdout():
                while chunk := await process.stdout.read(65536):
                    stdout.write(chunk); stdout.flush()

            reader = asyncio.create_task(drain_stdout())
            async with httpx.AsyncClient(base_url=url, timeout=15, trust_env=False, headers={"Origin": url}) as client:
                counter = 0

                async def http(method, path, *, body=None, expected=200):
                    nonlocal counter
                    response = await client.request(method, path, json=body) if body is not None else await client.request(method, path)
                    value = response.json()
                    recorded = redact_http_value(path, value)
                    counter += 1
                    (directory / f"http-{counter:04d}.json").write_text(json.dumps({"method": method, "path": path,
                        "request": body, "status": response.status_code, "response": recorded}, indent=2), encoding="utf8")
                    assert response.status_code == expected, f"HTTP {method} {path}: {response.status_code}; evidence retained"
                    return value

                health = await http("GET", "/api/health")
                assert health["backend"] == "go" and health["service"] == "rimgovernor" and health["pid"] == process.pid
                bootstrap = await http("GET", "/api/player/session")
                assert bootstrap["mode"] == "explicit-player" and bootstrap["token"]
                client.headers["X-RimGovernor-Player"] = bootstrap["token"]
                yield http
        finally:
            try:
                await joined_shutdown(process, record)
            finally:
                if reader is not None:
                    if process.returncode is None:
                        reader.cancel()
                    await asyncio.gather(reader, return_exceptions=True)


async def poll(http, path, predicate, timeout=45):
    async with asyncio.timeout(timeout):
        while True:
            value = await http("GET", path)
            if predicate(value):
                return value
            await asyncio.sleep(.25)


async def run(root: Path, output: Path, binary: Path, *, go_source: str, go_sha256: str, headless=True) -> bool:
    validate_go_handoff(go_source, go_sha256)
    assert Path("/.dockerenv").is_file(), "Run network/game processes only in container_scenario.py Docker worker"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source, "expected_binary_sha256": go_sha256, "scope": "Explicit HTTP building admission, joined exclusive handoffs, ordinary pawn completion and disabled same-DB restart reconciliation."}
    evidence = Evidence(output)
    launched = False
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        gabs = Path(gabs_executable(root, configuration))
        profile = root / ("headless-profile" if headless else "profile")
        private = output / "rimgovernor-go"
        shutil.copyfile(binary, private); private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)
        database = output / "service.sqlite"
        assert not database.exists()

        async def wire(bridge, label, name, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + name, {"request": json.dumps(request)})))

        async def identity_read(bridge, label):
            return outcome(await wire(bridge, label, "lifecycle_read_identity", {}), "loaded")

        async def buildings(bridge, label):
            anchor = {"x": site["x"], "z": site["z"]}
            value = outcome(await wire(bridge, label, "observations_list_buildings", {"scope": {"expectedIdentity": identity},
                "defNames": [site["defName"]], "category": "all", "region": {"minimum": anchor, "maximum": anchor}, "page": {"limit": 16}}), "observed")
            assert value["context"]["identity"] == identity and value["completeness"]["page"]["complete"] is True
            assert int(value["completeness"]["unreadable"]) == 0
            rows = value.get("buildings", [])
            assert len(rows) == int(value["completeness"]["matched"]) == int(value["completeness"]["returned"])
            for row in rows:
                assert row["building"]["position"] == anchor and row["stuff"] == site["stuff"]
            return rows

        async def capture(bridge, label, baseline=None, phase=None):
            catalog = payload(await evidence.call(bridge, label + "-capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] is True and catalog["truncated"] is False
            assert catalog["returnedCount"] == catalog["totalCount"] == len(catalog["capabilities"])
            caps = {row["id"]: set(row["aliases"]) for row in catalog["capabilities"]}
            request = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None:
                request["afterSequence"] = baseline
            journal = payload(await evidence.call(bridge, label + "-events", "rimbridge/list_operation_events", request))
            events = journal["events"]
            assert events and len(events) < 5000, "Journal page may be truncated; cannot claim complete history"
            if baseline is not None:
                report.setdefault("traces", {})[label] = {"baseline": baseline, "last_sequence": max(r["Sequence"] for r in events),
                    "operations": audit(events, baseline, caps, phase), "capabilities": {key: sorted(value) for key, value in caps.items()}}
            return max(row["Sequence"] for row in events)

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id); launched = True
            await bridge.connect()
            names = await discovery(bridge, evidence)
            fixtures = {"test/guarded_construction_prepare", "test/guarded_construction_control"}
            inventory = json.loads((Path(__file__).resolve().parents[1] / "contracts/domain-inventory.json").read_text())
            rows = inventory["native_surface"]["tools"]
            production = {row["name"] for row in rows if row["build_role"] == "production"}
            all_fixtures = {row["name"] for row in rows if row["build_role"] == "fixture"}
            validate_discovery(names, production, all_fixtures, fixtures)
            report["discovery"] = {"production": len(production), "fixtures": len(fixtures), "names": names}
            async with asyncio.timeout(180):
                await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            loaded = await identity_read(bridge, "before")
            identity, tick = loaded["context"]["identity"], int(loaded["context"]["tick"])
            assert loaded["paused"] is True
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 1}))
            assert prepared["success"] is True and len(prepared["sites"]) == 1
            assert all(prepared[key] == identity[key] for key in identity)
            site = prepared["sites"][0]; report["prepared"] = prepared
            assert not await buildings(bridge, "initial-empty")
            baseline = await capture(bridge, "setup")

        async def ready(http, expected_tick):
            value = await poll(http, "/api/state", lambda v: v.get("connected") is True and v.get("game", {}).get("stale") is False)
            assert value["identity"] == identity and value["game"]["paused"] is True and value["game"]["tick"] == expected_tick
            return value

        async with service(private, gabs, configuration, profile, database, output / "submit", report) as http:
            await ready(http, tick)
            building = http_building(site)
            body = {"requestId": "fixture-submit-1", "expected": identity, "building": building}
            submission = await http("POST", "/api/buildings/plans", body=body, expected=201)
            assert submission["requestId"] == body["requestId"] and submission["expected"] == identity and submission["building"] == building
            assert await http("GET", "/api/buildings/submission?requestId=fixture-submit-1") == submission
            assert await http("POST", "/api/buildings/plans", body=body) == submission
            assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            assert action(await http("GET", "/api/plan?id=" + submission["planId"]), submission)["receipt"] is None
            await ready(http, tick)
            report["submission"] = submission
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            baseline = await capture(bridge, "submit-service", baseline, "submit")
            assert not await buildings(bridge, "submit-empty")
            loaded = await identity_read(bridge, "submit-paused")
            assert loaded["context"]["identity"] == identity and int(loaded["context"]["tick"]) == tick and loaded["paused"] is True
            baseline = await capture(bridge, "submit-orchestrator", baseline, "orchestrator")

        async with service(private, gabs, configuration, profile, database, output / "execute", report) as http:
            await ready(http, tick)
            assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            granted = await http("POST", "/api/player/control/acquire", body={"requestId": "fixture-acquire-1", "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": "0"})
            assert granted["record"]["phase"] == "granted" and granted["record"]["direction"] == "1"
            assert int(granted["record"]["nativeGeneration"]) > 0 and granted["state"]["enabled"] is True and granted["state"]["observationKnown"] is True
            assert granted["state"]["generation"]["native"] == granted["record"]["nativeGeneration"]
            plan = await poll(http, "/api/plan?id=" + submission["planId"], lambda value: action(value, submission)["stage"] == "awaiting_observation")
            verify_progress(plan, submission, False); report["admitted_plan"] = plan
            await ready(http, tick)
            manual = await http("POST", "/api/player/control/manual", body={"requestId": "fixture-manual-1", "expected": identity})
            assert manual["record"]["phase"] == "disabled" and manual["record"]["nativeGeneration"] == "0" and manual["state"]["enabled"] is False
            assert (await http("GET", "/api/player/control"))["record"]["requestId"] == "fixture-manual-1"
            verify_progress(await http("GET", "/api/plan?id=" + submission["planId"]), submission, False)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            baseline = await capture(bridge, "execute-service", baseline, "execute")
            rows = await buildings(bridge, "one-blueprint")
            assert len(rows) == 1 and rows[0]["status"] == "blueprint"
            report["blueprint_id"] = rows[0]["building"]["id"]
            loaded = await identity_read(bridge, "execution-paused")
            assert loaded["context"]["identity"] == identity and int(loaded["context"]["tick"]) == tick and loaded["paused"] is True
            runtime = ScenarioClock(bridge, report)
            for window in range(13):
                rows = await buildings(bridge, f"pawn-work-{window}")
                assert len(rows) == 1 and rows[0]["status"] in {"blueprint", "frame", "built"}
                if rows[0]["status"] == "built":
                    report["completed_building"] = rows[0]
                    break
                assert window < 12, "Ordinary pawn work did not complete within7200ticks"
                await advance_game(runtime, 600, report, timeout=180)
            final = await identity_read(bridge, "completed-paused")
            assert final["context"]["identity"] == identity and final["paused"] is True and int(final["context"]["tick"]) > tick
            final_tick = int(final["context"]["tick"])
            baseline = await capture(bridge, "pawn-orchestrator", baseline, "orchestrator")
        async with service(private, gabs, configuration, profile, database, output / "restart", report) as http:
            await ready(http, final_tick)
            assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            plan = await poll(http, "/api/plan?id=" + submission["planId"], lambda value: action(value, submission)["stage"] == "completed")
            verify_progress(plan, submission, True); report["completed_plan"] = plan
            assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            await ready(http, final_tick)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "restart-service", baseline, "restart")
            final = await identity_read(bridge, "final")
            assert final["context"]["identity"] == identity and final["paused"] is True and int(final["context"]["tick"]) == final_tick
        report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
    finally:
        incomplete = any(not phase.get("joined") for phase in report.get("service_phases", []))
        if launched and not incomplete:
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report.update(passed=False, cleanup_error=repr(error))
        elif incomplete:
            report.update(passed=False, resources_retained=True)
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-binary", type=Path, required=True)
    add_handoff_arguments(parser)
    parser.add_argument("--rendered", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-building-service-acceptance", args.go_binary, go_source=args.go_source, go_sha256=args.go_sha256, headless=not args.rendered)) else 1)

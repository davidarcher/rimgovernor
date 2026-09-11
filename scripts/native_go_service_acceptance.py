"""Read-only Go service against a private game; run through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
from urllib.parse import urlsplit

import httpx

from native_package_acceptance import Evidence, bridge_session, gabs_executable, payload, prepare, prepare_rendered
from native_protobuf_acceptance import proto


def trace(events: list[dict], baseline: int, capabilities: dict[str, set[str]]) -> list[str]:
    ordered = sorted(events, key=lambda row: row["Sequence"])
    sequences = [row["Sequence"] for row in ordered]
    assert sequences and sequences == list(range(baseline + 1, sequences[-1] + 1)), "Native operation event history has a gap"
    reads = {"rimgovernor/lifecycle_read_identity", "rimgovernor/observations_read_status"}
    diagnostics = {"rimbridge/list_operation_events"}
    operations: dict[str, str] = {}
    for row in ordered:
        identifier = row.get("CapabilityId")
        if not identifier:
            assert not row.get("OperationId"), "Operation event has no capability attribution"
            continue  # Journal diagnostics still participate in the sequence check.
        aliases = capabilities.get(identifier, set())
        selected = aliases & (reads | diagnostics)
        assert selected, f"Unexpected native capability during service ownership: {identifier}: {aliases}"
        if selected & reads:
            operation = row["OperationId"]
            name = next(iter(selected & reads))
            assert operations.setdefault(operation, name) == name
    names = list(operations.values())
    assert names.count("rimgovernor/observations_read_status") >= 2
    assert names.count("rimgovernor/lifecycle_read_identity") >= 4
    return names


def verify_state(value: dict, identity: dict, tick: int) -> datetime:
    assert value["connected"] is True and value["mode"] == "manual"
    assert value["status"]["label"] == "Read-only observation" and value["sessionId"]
    assert value["identity"] == identity and value["generation"] is None and value["activePlanId"] is None
    game = value["game"]
    assert type(game["tick"]) is int and game["tick"] == tick
    assert game["paused"] is True and game["stale"] is False
    stamp = datetime.fromisoformat(game["observedAt"].replace("Z", "+00:00"))
    assert stamp.tzinfo is not None
    return stamp


async def service(binary: Path, gabs: Path, configuration: Path, output: Path,
                  identity: dict, tick: int, report: dict) -> None:
    stdout_path = output / "service-stdout.txt"
    with (output / "service-stderr.txt").open("wb") as stderr:
        process = await asyncio.create_subprocess_exec(str(binary.resolve()), "serve", "--read-only",
            "--gabs", str(gabs.resolve()), "--config", str(configuration.resolve()), "--game", "rimgovernor-trial",
            "--state", str((output / "service.sqlite").resolve()), "--listen", "127.0.0.1:0",
            "--refresh", "1s", "--timeout", "15s", stdout=asyncio.subprocess.PIPE, stderr=stderr)
        report["service_pid"] = process.pid
        try:
            async with asyncio.timeout(120):
                line = await process.stdout.readline()
            stdout_path.write_bytes(line)
            prefix = "RimGovernor Go read-only service: "
            assert line.decode().startswith(prefix), "Go service did not publish its loopback URL"
            url = line.decode().strip()[len(prefix):]
            address = urlsplit(url)
            assert address.scheme == "http" and address.hostname == "127.0.0.1" and address.port
            report["service_url"] = url
            async with httpx.AsyncClient(base_url=url, timeout=5, trust_env=False) as client:
                health = await client.get("/api/health")
                (output / "health.json").write_bytes(health.content)
                assert health.status_code == 200
                assert health.json()["service"] == "rimgovernor" and health.json()["backend"] == "go"
                assert health.json()["pid"] == process.pid
                samples = []
                first_stamp = None
                poll = 0
                async with asyncio.timeout(45):
                    while len(samples) < 2:
                        response = await client.get("/api/state")
                        assert response.status_code == 200
                        value = response.json()
                        (output / f"state-poll-{poll:03d}.json").write_bytes(response.content)
                        poll += 1
                        if value.get("connected") is True:
                            stamp = verify_state(value, identity, tick)
                            if first_stamp is None or stamp > first_stamp:
                                if samples:
                                    assert value["sessionId"] == samples[0]["sessionId"]
                                samples.append(value)
                                first_stamp = stamp
                        await asyncio.sleep(.25)
                report["http_samples"] = samples
        finally:
            if process.returncode is None:
                process.terminate()
            try:
                async with asyncio.timeout(20):
                    remainder, _ = await process.communicate()
            except TimeoutError:
                process.kill()
                remainder, _ = await process.communicate()
                report["forced_service_kill"] = True
            with stdout_path.open("ab") as log:
                log.write(remainder)
            report["service_exit"] = process.returncode
        assert process.returncode == 0 and not report.get("forced_service_kill"), "Go service did not join cleanly"


async def run(root: Path, output: Path, binary: Path, *, headless: bool) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    report: dict = {"passed": False, "scope": "Native typed status through Go read-only HTTP polling, exclusive GABS handoff, unchanged game and joined shutdown."}
    evidence = Evidence(output)
    configuration = prepare(root) if headless else prepare_rendered(root)
    gabs = Path(gabs_executable(root, configuration))
    private = output / "rimgovernor-go"
    shutil.copyfile(binary, private)
    private.chmod(0o700)
    report["binary_sha256"] = hashlib.sha256(private.read_bytes()).hexdigest()
    launched = False
    try:
        async with bridge_session(gabs, configuration) as bridge:
            launched = True
            await bridge.core("games_start", gameId=bridge.game_id)
            await bridge.connect()
            await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready",
                {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            before = proto(payload(await evidence.call(bridge, "identity-before", "rimgovernor/lifecycle_read_identity", {"request": "{}"})))["loaded"]
            identity, tick = before["context"]["identity"], int(before["context"]["tick"])
            assert before["paused"] is True
            report["before"] = before
            catalog = payload(await evidence.call(bridge, "capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] is True and catalog["truncated"] is False
            assert catalog["returnedCount"] == catalog["totalCount"] == len(catalog["capabilities"])
            capabilities = {row["id"]: set(row["aliases"]) for row in catalog["capabilities"]}
            baseline_events = payload(await evidence.call(bridge, "events-before", "rimbridge/list_operation_events",
                {"limit": 5000, "includeDiagnostics": True}))["events"]
            baseline = max(row["Sequence"] for row in baseline_events)
            report["baseline_sequence"] = baseline
        # Joining the first GABS process releases attachment before the Go service opens its own.
        await service(private, gabs, configuration, output, identity, tick, report)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            events = payload(await evidence.call(bridge, "events-after", "rimbridge/list_operation_events",
                {"limit": 5000, "afterSequence": baseline, "includeDiagnostics": True}))["events"]
            report["native_reads"] = trace(events, baseline, capabilities)
            after = proto(payload(await evidence.call(bridge, "identity-after", "rimgovernor/lifecycle_read_identity", {"request": "{}"})))["loaded"]
            assert after["context"]["identity"] == identity and int(after["context"]["tick"]) == tick
            assert after["paused"] is True and after["context"].get("nativeGeneration") == before["context"].get("nativeGeneration")
            report["after"] = after
        report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
    finally:
        if launched:
            try:
                async with asyncio.timeout(90):
                    async with bridge_session(gabs, configuration) as bridge:
                        report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report["passed"] = False
                report["cleanup_error"] = repr(error)
        report["artifacts"] = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in output.rglob("*") if p.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-service", type=Path, required=True)
    parser.add_argument("--rendered", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-service-acceptance",
        args.go_service, headless=not args.rendered)) else 1)

"""Go HTTP temporary-draft acceptance; native processes run only in container_scenario.py."""
from __future__ import annotations
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import shlex
import shutil
import sqlite3
import sys
import httpx
from mcp.types import CallToolResult
from native_building_service_acceptance import (Evidence, bridge_session, gabs_executable, payload, prepare,
    prepare_rendered, package_files, proto, outcome, discovery, validate_discovery, service, poll,
    READS, DIAGNOSTICS, CONTROL, EXECUTE, add_handoff_arguments, validate_go_handoff, verify_go_binary)
from native_draft_acceptance import pawn_row
from native_guarded_construction_acceptance import ScenarioClock
from rimgovernor.native_scenario import advance_game

RELEASE = "rimgovernor/operations_release_owned_draft"
FIXTURE = "test/guarded_construction_control"
MODES = ("ordinary", "lost_execute", "override")


def progress(plan: dict, submission: dict) -> dict:
    assert plan["id"] == submission["planId"] and plan["revision"] == submission["revision"]
    assert len(plan["actions"]) == 1
    action = plan["actions"][0]
    assert action["id"] == submission["actionId"] and action["kind"] == "owned_draft"
    assert action["draft"] == submission["draft"] and "building" not in action
    return action["progress"]


def verify_terminal(plan: dict, submission: dict, mode: str) -> None:
    value = progress(plan, submission)
    assert value["attempt"] == "1" and value["unresolved"] is False
    assert value["draftCleanup"] == {"stage": "superseded" if mode == "override" else "released"}
    if mode != "override":
        assert value["stage"] == "completed" and value["effect"] == "completed"
    else:
        assert value["stage"] in {"completed", "unsuccessful", "cancelled"}, value
    encoded = json.dumps(plan)
    assert all(key not in encoded for key in ("claimId", "leaseId", "controllerSessionId", "expectedSnapshotToken"))


def native_name(request: dict) -> str | None:
    if request.get("method") == "tools/call" and request.get("params", {}).get("name") == "games_call_tool":
        return request["params"]["arguments"]["tool"]
    return None


def wire_response(response: dict) -> dict:
    assert "result" in response and "error" not in response, response
    return proto(payload(CallToolResult.model_validate(response["result"])))


def issued_claim(response: dict) -> dict:
    receipt = outcome(wire_response(response), "receipt")
    assert set(receipt) == {"attempt", "admittedContext", "authorizingOwner", "applied"}, receipt
    job = outcome(receipt["applied"]["observed"], "job")
    assert job["drafted"] is True and job["issued"] is True and job["verified"] is True
    assert job["draftClaimId"] and job["draftOwner"] and job["resultingSnapshotToken"]
    return job


def audit(events: list[dict], baseline: int, capabilities: dict[str, set[str]], mode: str, pawn_id: str) -> dict:
    ordered = sorted(events, key=lambda row: row["Sequence"])
    seq = [row["Sequence"] for row in ordered]
    assert seq and seq == list(range(baseline + 1, seq[-1] + 1)), "Incomplete SDK event sequence"
    allowed = READS | DIAGNOSTICS | {"rimgovernor/observations_list_pawns", "rimgovernor/operations_preview"}
    if mode != "restart": allowed |= {CONTROL, EXECUTE, RELEASE}
    if mode == "override": allowed |= {FIXTURE}
    operations, completed = {}, {}
    for row in ordered:
        cap = row.get("CapabilityId")
        if not cap:
            assert not row.get("OperationId"), "Unattributed operation"
            continue
        names = capabilities.get(cap, set()) & allowed
        assert len(names) == 1, (mode, cap, capabilities.get(cap))
        name = next(iter(names)); identifier = row["OperationId"]
        assert operations.setdefault(identifier, name) == name
        if row.get("EventType") == "operation.completed": completed[identifier] = row
    assert all(name in DIAGNOSTICS or key in completed for key, name in operations.items()), "Native operation did not finish within joined phase"
    names = list(operations.values())
    assert "rimgovernor/lifecycle_read_identity" in names
    assert names.count(EXECUTE) == (0 if mode == "restart" else 1)
    assert names.count(FIXTURE) == (2 if mode == "override" else 0)
    controls = []
    for identifier, name in operations.items():
        if name in {EXECUTE, RELEASE, CONTROL, FIXTURE}:
            row = completed[identifier]
            assert row["Success"] is True and row["HasResult"] is True
            arguments = row["Metadata"]["arguments"]
            if name == FIXTURE:
                assert arguments["operation"] == "draft" and arguments["pawnId"] == pawn_id
            else:
                request = json.loads(arguments["request"])
                if name == EXECUTE:
                    operation = request["operation"]
                    assert set(operation) == {"setDrafted"}
                    assert operation["setDrafted"]["pawn"]["entityId"] == pawn_id
                    assert operation["setDrafted"]["drafted"] is True and operation["setDrafted"]["allowPersistentDraft"] is False
                elif name == RELEASE:
                    assert request["pawn"]["entityId"] == pawn_id and request["expectedClaimId"] and request["originalOwner"]
                else: controls.append(next(iter(request)))
    if mode == "restart": assert not controls and RELEASE not in names
    else:
        assert controls.count("acquire") == 1 and controls.count("revoke") <= 1
        assert set(controls) <= {"acquire", "renew", "revoke"}
        if mode != "override": assert names.count(RELEASE) == 1
    return {"operations": names, "last_sequence": seq[-1], "baseline": baseline}


def proxy_summary(path: Path, mode: str, pawn_id: str) -> dict:
    records = [json.loads(line) for line in path.read_text(encoding="utf8").splitlines()]
    requests = {json.dumps(row["message"]["id"]): row["message"] for row in records
                if row["direction"] == "to-native" and "id" in row["message"]}
    forwarded_executes = [row for row in records if row["direction"] == "to-native" and native_name(row["message"]) == EXECUTE]
    executes = [json.dumps(row["message"]["id"]) for row in forwarded_executes]
    assert len(executes) == (0 if mode == "restart" else 1), "Native Execute repeated or missing"
    result = {"native_execute_count": len(executes)}
    if executes:
        replies = [row["message"] for row in records if row["direction"] == "from-native" and json.dumps(row["message"].get("id")) == executes[0]]
        assert len(replies) == 1
        claim = issued_claim(replies[0]); assert claim["pawnId"] == pawn_id
        result["claim_evidence"] = claim
        if mode != "override":
            releases = [key for key, request in requests.items() if native_name(request) == RELEASE]
            assert len(releases) == 1
            replies = [row["message"] for row in records if row["direction"] == "from-native" and json.dumps(row["message"].get("id")) == releases[0]]
            assert len(replies) == 1
            released = outcome(wire_response(replies[0]), "released")
            assert released["request"]["expectedClaimId"] == claim["draftClaimId"]
            assert released["request"]["originalOwner"]["controllerSessionId"] == claim["draftOwner"]
            assert released["observed"]["pawnId"] == pawn_id and released["observed"]["drafted"] is False
            assert released["observed"]["verified"] is True and released["observed"]["issued"] is True
            result["release_evidence"] = released
    faults = [row for row in records if row["direction"] == "fault"]
    assert len(faults) == (1 if mode in {"lost_execute", "override"} else 0)
    if faults: assert faults[0]["message"]["mode"] == mode
    return result


async def proxy(real: Path, log: Path, mode: str, fixture: dict, argv: list[str]) -> int:
    """One same-owner MCP connection; only a successful draft reply can trigger the fault."""
    assert Path("/.dockerenv").is_file()
    assert mode in {*MODES, "restart"}
    child = await asyncio.create_subprocess_exec(str(real), *argv, stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=sys.stderr, limit=4 * 1024 * 1024)
    requests, held = {}, None
    injected = 0
    fired = False
    def record(direction, message):
        with log.open("a", encoding="utf8") as stream: stream.write(json.dumps({"direction": direction, "message": message}) + "\n")
    async def forward(message):
        record("to-native", message)
        child.stdin.write((json.dumps(message) + "\n").encode()); await child.stdin.drain()
    def deliver(message):
        record("to-go", message); sys.stdout.write(json.dumps(message) + "\n"); sys.stdout.flush()
    reader = asyncio.StreamReader(limit=4 * 1024 * 1024)
    transport, _ = await asyncio.get_running_loop().connect_read_pipe(lambda: asyncio.StreamReaderProtocol(reader), sys.stdin.buffer)
    async def incoming():
        while line := await reader.readline():
            message = json.loads(line)
            if "id" in message: requests[json.dumps(message["id"])] = message
            await forward(message)
        child.stdin.close()
    async def outgoing():
        nonlocal fired, held, injected
        while line := await child.stdout.readline():
            message = json.loads(line); record("from-native", message)
            identifier = message.get("id")
            if isinstance(identifier, str) and identifier.startswith("draft-fixture-"):
                value = payload(CallToolResult.model_validate(message["result"]))
                assert value["success"] is True and value["pawnId"] == fixture["pawnId"]
                assert value["before"] is (injected == 1) and value["drafted"] is (injected == 2)
                if injected == 1:
                    injected = 2
                    await forward({"jsonrpc": "2.0", "id": "draft-fixture-2", "method": "tools/call", "params": {
                        "name": "games_call_tool", "arguments": {"gameId": "rimgovernor-trial", "tool": FIXTURE, "arguments": fixture}}})
                else:
                    record("fault", {"mode": mode, "injected_setters": 2}); deliver(held); held = None
                continue
            request = requests.get(json.dumps(identifier), {})
            if not fired and native_name(request) == EXECUTE and mode in {"lost_execute", "override"}:
                claim = issued_claim(message); assert claim["pawnId"] == fixture["pawnId"]
                fired = True
                if mode == "lost_execute":
                    record("fault", {"mode": mode, "raw_success_retained": True})
                    deliver({"jsonrpc": "2.0", "id": identifier, "error": {"code": -32000, "message": "Acceptance fault: successful native draft reply withheld"}})
                else:
                    held = message; injected = 1
                    await forward({"jsonrpc": "2.0", "id": "draft-fixture-1", "method": "tools/call", "params": {
                        "name": "games_call_tool", "arguments": {"gameId": "rimgovernor-trial", "tool": FIXTURE, "arguments": fixture}}})
            else: deliver(message)
        assert held is None, "Player override injection incomplete"
    tasks = [asyncio.create_task(incoming()), asyncio.create_task(outgoing())]
    try:
        await asyncio.gather(*tasks)
        async with asyncio.timeout(30): await child.wait()
        return child.returncode
    finally:
        transport.close()
        for task in tasks: task.cancel()
        if child.returncode is None:
            child.terminate()
            async with asyncio.timeout(30): await child.wait()


def proxy_executable(directory: Path, real: Path, mode: str, fixture: dict) -> Path:
    directory.mkdir()
    config = directory / "fixture.json"; config.write_text(json.dumps(fixture), encoding="utf8")
    wrapper = directory / "gabs-draft-proxy"
    command = [sys.executable, str(Path(__file__).resolve()), "--proxy-real", str(real.resolve()),
        "--proxy-log", str((directory / "wire.jsonl").resolve()), "--proxy-mode", mode, "--proxy-fixture", str(config.resolve()), "--"]
    wrapper.write_text("#!/bin/sh\nexec " + shlex.join(command) + ' "$@"\n', encoding="utf8"); wrapper.chmod(0o700)
    return wrapper


async def lose_http_ack(http, url: str, path: str, body: dict, evidence_path: Path) -> None:
    bootstrap = await http("GET", "/api/player/session")
    async with httpx.AsyncClient(base_url=url, timeout=15, trust_env=False, headers={"Origin": url,
        "X-RimGovernor-Player": bootstrap["token"]}) as client:
        async with client.stream("POST", path, json=body) as response:
            evidence_path.write_text(json.dumps({"method": "POST", "path": path, "request": body,
                "status": response.status_code, "response_body_delivered_to_caller": False}, indent=2), encoding="utf8")
            assert response.status_code in {200, 201}
            # Deliberately close without consuming the acknowledgment body. Recover by GET only.


async def run(root: Path, output: Path, binary: Path, *, go_source: str, go_sha256: str, headless=True) -> bool:
    validate_go_handoff(go_source, go_sha256)
    assert Path("/.dockerenv").is_file(), "Use container_scenario.py; no host listeners"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source, "expected_binary_sha256": go_sha256,
        "scope": "Actual Go HTTP temporary draft, native claim/release, deterministic player override and successful Execute reply loss; disabled same-DB restarts."}
    evidence = Evidence(output); launched = False
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text(encoding="utf8"))["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        gabs = Path(gabs_executable(root, configuration)); profile = root / ("headless-profile" if headless else "profile")
        private = output / "rimgovernor-go"; shutil.copyfile(binary, private); private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)
        database = output / "service.sqlite"; assert not database.exists()
        async def wire(bridge, label, method, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + method, {"request": json.dumps(request)})))
        async def identity_read(bridge, label): return outcome(await wire(bridge, label, "lifecycle_read_identity", {}), "loaded")
        async def read_pawn(bridge, label):
            return pawn_row(await wire(bridge, label, "observations_list_pawns", {"scope": {"expectedIdentity": identity},
                "filter": {"ids": [pawn_id]}, "page": {"limit": 1}}), identity, pawn_id)
        async def capture(bridge, label, baseline=None, mode=None):
            catalog = payload(await evidence.call(bridge, label + "-catalog", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] is True and catalog["truncated"] is False
            assert catalog["returnedCount"] == catalog["totalCount"] == len(catalog["capabilities"])
            caps = {row["id"]: set(row["aliases"]) for row in catalog["capabilities"]}
            query = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None: query["afterSequence"] = baseline
            events = payload(await evidence.call(bridge, label + "-events", "rimbridge/list_operation_events", query))["events"]
            assert events and len(events) < 5000
            if baseline is not None: report.setdefault("traces", {})[label] = audit(events, baseline, caps, mode, pawn_id)
            return max(row["Sequence"] for row in events)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id); launched = True; await bridge.connect()
            names = await discovery(bridge, evidence)
            inventory = json.loads((Path(__file__).resolve().parents[1] / "contracts/domain-inventory.json").read_text(encoding="utf8"))["native_surface"]["tools"]
            validate_discovery(names, {row["name"] for row in inventory if row["build_role"] == "production"},
                {row["name"] for row in inventory if row["build_role"] == "fixture"}, {"test/guarded_construction_prepare", FIXTURE})
            report["discovery"] = names
            async with asyncio.timeout(180):
                await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            loaded = await identity_read(bridge, "initial"); identity = loaded["context"]["identity"]; tick = int(loaded["context"]["tick"])
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 1}))
            assert prepared["success"] is True and all(prepared[key] == identity[key] for key in identity)
            pawn_id = prepared["pawnId"]; report["prepared"] = prepared
            before = await read_pawn(bridge, "initial-pawn"); assert before["drafted"] is False and before["draftClaim"] == {"unowned": {}}
        fixture = {"operation": "draft", **identity, "pawnId": pawn_id}
        async def ready(http):
            state = await poll(http, "/api/state", lambda value: value.get("connected") is True and value.get("game", {}).get("stale") is False)
            assert state["identity"] == identity and state["game"]["paused"] is True and state["game"]["tick"] == tick
        for mode in MODES:
            async with bridge_session(gabs, configuration) as bridge:
                await bridge.connect(); baseline = await capture(bridge, mode + "-baseline")
            proxy_dir = output / (mode + "-proxy"); proxy_path = proxy_executable(proxy_dir, gabs, mode, fixture)
            async with service(private, proxy_path, configuration, profile, database, output / mode, report) as http:
                await ready(http)
                current = await http("GET", "/api/player/control"); assert current["state"]["enabled"] is False
                direction = current["record"]["direction"] if current["record"] else "0"
                body = {"requestId": mode + "-submit", "expected": identity, "draft": {"pawnId": pawn_id}}
                if mode == "ordinary":
                    await lose_http_ack(http, report["service_phases"][-1]["url"], "/api/drafts/plans", body, output / "lost-submit-ack.json")
                    submission = await http("GET", "/api/drafts/submission?requestId=" + body["requestId"])
                else: submission = await http("POST", "/api/drafts/plans", body=body, expected=201)
                assert all(submission[key] == body[key] for key in body)
                assert await http("GET", "/api/drafts/submission?requestId=" + body["requestId"]) == submission
                waiting = progress(await http("GET", "/api/plan?id=" + submission["planId"]), submission)
                assert waiting["stage"] == "pending" and waiting["attempt"] == "0" and waiting["receipt"] is None
                acquire = {"requestId": mode + "-acquire", "expected": identity, "planId": submission["planId"],
                    "revision": submission["revision"], "expectedDirection": direction}
                if mode == "ordinary":
                    await lose_http_ack(http, report["service_phases"][-1]["url"], "/api/player/control/acquire", acquire, output / "lost-acquire-ack.json")
                    grant = await http("GET", "/api/player/control?requestId=" + acquire["requestId"])
                else: grant = await http("POST", "/api/player/control/acquire", body=acquire)
                assert grant["record"]["phase"] == "granted" and grant["record"]["direction"] == str(int(direction) + 1)
                plan = await poll(http, "/api/plan?id=" + submission["planId"], lambda value:
                    progress(value, submission).get("draftCleanup", {}).get("stage") == ("superseded" if mode == "override" else "released"), timeout=90)
                verify_terminal(plan, submission, mode)
                manual = await http("POST", "/api/player/control/manual", body={"requestId": mode + "-manual", "expected": identity})
                assert manual["state"]["enabled"] is False and manual["record"]["phase"] == "disabled"
                report.setdefault("plans", {})[mode] = plan
                await ready(http)
            report.setdefault("proxy", {})[mode] = proxy_summary(proxy_dir / "wire.jsonl", mode, pawn_id)
            async with bridge_session(gabs, configuration) as bridge:
                await bridge.connect(); await capture(bridge, mode + "-service", baseline, mode)
                row = await read_pawn(bridge, mode + "-native-after")
                assert row["drafted"] is (mode == "override") and row["draftClaim"] == {"unowned": {}}
                after = await identity_read(bridge, mode + "-paused"); assert after["context"]["identity"] == identity and int(after["context"]["tick"]) == tick
                baseline = await capture(bridge, mode + "-restart-baseline")
            restart_proxy_dir = output / (mode + "-restart-proxy")
            restart_proxy = proxy_executable(restart_proxy_dir, gabs, "restart", fixture)
            async with service(private, restart_proxy, configuration, profile, database, output / (mode + "-restart"), report) as http:
                await ready(http); assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
                recovered = await http("GET", "/api/drafts/submission?requestId=" + body["requestId"]); assert recovered == submission
                verify_terminal(await http("GET", "/api/plan?id=" + submission["planId"]), submission, mode)
                await asyncio.sleep(2)
                assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            report["proxy"][mode + "-restart"] = proxy_summary(restart_proxy_dir / "wire.jsonl", "restart", pawn_id)
            async with bridge_session(gabs, configuration) as bridge:
                await bridge.connect(); await capture(bridge, mode + "-restart-service", baseline, "restart")
                row = await read_pawn(bridge, mode + "-restart-native")
                assert row["drafted"] is (mode == "override") and row["draftClaim"] == {"unowned": {}}
                if mode == "ordinary":
                    await advance_game(ScenarioClock(bridge, report), 60, report, timeout=120)
                    after = await identity_read(bridge, "advanced"); assert after["paused"] is True and after["context"]["identity"] == identity
                    assert int(after["context"]["tick"]) == tick + 60; tick += 60
        assert database.is_file(), "Service did not retain its requested database"
        with sqlite3.connect(database) as connection: assert connection.execute("PRAGMA integrity_check").fetchone() == ("ok",)
        report["sqlite_integrity"] = "ok"; report["passed"] = True
    except BaseException as error: report["error"] = repr(error)
    finally:
        incomplete = any(not phase.get("joined") for phase in report.get("service_phases", []))
        if launched and not incomplete:
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error: report.update(passed=False, cleanup_error=repr(error))
        elif incomplete: report.update(passed=False, resources_retained=True)
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    if "--proxy-real" in sys.argv:
        parser = argparse.ArgumentParser(); parser.add_argument("--proxy-real", type=Path); parser.add_argument("--proxy-log", type=Path)
        parser.add_argument("--proxy-mode", choices=(*MODES, "restart")); parser.add_argument("--proxy-fixture", type=Path)
        args, tail = parser.parse_known_args(); tail = tail[1:] if tail[:1] == ["--"] else tail
        raise SystemExit(asyncio.run(proxy(args.proxy_real, args.proxy_log, args.proxy_mode, json.loads(args.proxy_fixture.read_text(encoding="utf8")), tail)))
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path); parser.add_argument("--go-binary", type=Path, required=True)
    add_handoff_arguments(parser); parser.add_argument("--rendered", action="store_true"); args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-draft-acceptance", args.go_binary,
        go_source=args.go_source, go_sha256=args.go_sha256, headless=not args.rendered)) else 1)

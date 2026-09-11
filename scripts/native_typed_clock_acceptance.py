"""Typed native clock gameplay acceptance; launch through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
from copy import deepcopy
import hashlib
import json
from pathlib import Path

from native_package_acceptance import (Evidence, bridge_session, check_startup_log,
    gabs_executable, object_value, package_files, payload, prepare, prepare_rendered)
from native_protobuf_acceptance import proto
from native_compatibility_acceptance import discovery
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.clock_control import HOLD_REASONS
from rimgovernor.native_scenario import advance_game, ScenarioInterrupted


POLICY = {"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.1,
    "minHealthFraction": 0.5, "hostileWithin": 40, "injuryStopCooldownMs": 0}
CLOCK_TOOLS = {"rimgovernor/clock_" + name for name in
    ("start", "renew", "change_speed", "pause", "read_status", "read_events", "read_attempt")}


def outcome(reply, case):
    assert set(reply) == {case}, f"Expected {case}: {reply}"
    return object_value(reply[case], case)


def integer(value):
    assert type(value) is int or (type(value) is str and value.isascii() and value.isdecimal()), value
    result = int(value)
    assert 0 <= result <= 2**64 - 1, value
    return result


def context(value):
    assert isinstance(value, dict) and isinstance(value.get("identity"), dict), value
    identity = value["identity"]
    assert all(type(identity.get(key)) is str and identity[key] for key in ("colonyId", "loadToken")), value
    assert type(identity.get("mapId")) is int and identity["mapId"] >= 0, value
    integer(value["tick"])
    return value


def project_status(status):
    """Adapt observed typed facts to advance_game's existing test protocol."""
    context(status["context"])
    cases = [key for key in ("running", "stopping", "stopped", "neverStarted", "unavailable") if key in status]
    assert len(cases) == 1 and cases[0] in ("running", "stopping", "stopped"), status
    case = cases[0]
    body, epoch = status[case], status[case]["epoch"]
    context(epoch["origin"])
    owner = epoch["owner"]
    assert type(owner.get("controllerSessionId")) is str and owner["controllerSessionId"], owner
    epoch_id = integer(owner["epoch"])
    assert epoch_id > 0, owner
    assert type(status.get("actualPaused")) is bool and type(status.get("nativeTickBoundary")) is bool, status
    result = {"native": deepcopy(status), "owner": owner["controllerSessionId"], "epoch": epoch_id,
        "active": case != "stopped", "startTick": integer(epoch["startTick"]),
        "lastTick": integer(epoch["lastTick"]), "tickDeadline": integer(epoch["tickDeadline"]),
        "newestCursor": integer(status["newestCursor"]), "paused": status["actualPaused"],
        "nativeTickBoundary": status["nativeTickBoundary"]}
    assert result["startTick"] <= result["lastTick"] <= result["tickDeadline"], result
    if case == "stopped":
        reason = body["reason"]
        assert type(reason) is str and reason.startswith("STOP_REASON_") and reason != "STOP_REASON_UNSPECIFIED", body
        assert type(body.get("pauseVerified")) is bool and body.get("actualPaused") is status["actualPaused"], body
        result.update(stopReason=reason.removeprefix("STOP_REASON_").lower(), pauseVerified=body["pauseVerified"])
    return result


def project_events(page, after):
    context(page["context"])
    assert page.get("gap") is False and integer(page["lostCount"]) == 0, page
    newest, next_cursor = (integer(page[key]) for key in ("newestCursor", "nextCursor"))
    assert after <= newest, page
    if "oldestCursor" in page:
        assert integer(page["oldestCursor"]) <= newest, page
    rows, previous = [], after
    for event in page.get("events", []):
        cursor = integer(event["cursor"])
        # Current producer is a contiguous durable journal. Missing evidence fails
        # this acceptance test instead of being treated as an empty page.
        assert cursor == previous + 1 and cursor <= newest, page
        previous = cursor
        context(event["context"])
        owner = event["owner"]
        assert type(owner.get("controllerSessionId")) is str and owner["controllerSessionId"], event
        row = {"native": deepcopy(event), "cursor": cursor, "owner": owner["controllerSessionId"],
            "epoch": integer(owner["epoch"])}
        assert row["epoch"] > 0, event
        if "stopped" in event:
            reason = event["stopped"]["reason"]
            assert reason.startswith("STOP_REASON_") and reason != "STOP_REASON_UNSPECIFIED", event
            row["kind"] = reason.removeprefix("STOP_REASON_").lower()
            if row["kind"] == "letter_pause":
                letter = event["stopped"].get("pause", {}).get("letter")
                assert isinstance(letter, dict) and type(letter.get("id")) is str and letter["id"], event
                # The native typed producer admits this evidence only from its
                # exact LetterStack.ReceiveLetter hook, never a status-list guess.
                row["event"] = {"letterId": letter["id"], "source": "LetterStack.ReceiveLetter"}
        else:
            cases = [key for key in ("started", "speedChanged", "notification", "alert", "injuryObserved",
                "hostilesCleared", "pauseFailed", "forcePauseWaiting", "forcePauseCleared") if key in event]
            assert len(cases) == 1, event
            row["kind"] = cases[0]
        rows.append(row)
    assert next_cursor == previous, page
    assert rows or next_cursor == newest, "Empty page before newest retained cursor"
    return {"native": deepcopy(page), "events": rows, "nextCursor": next_cursor, "gap": False}


class TypedScenarioClock:
    """Test-only typed transport adapter; advance_game owns every tick wait."""
    def __init__(self, wire, identity, owner, report):
        self.wire, self.identity, self.owner, self.report = wire, deepcopy(identity), owner, report
        self.grant, self.counter, self.cursor, self.epoch = None, 0, 0, 0
        self.hold, self.on_started = None, None
        self.controls = []

    def precondition(self):
        assert self.grant is not None
        self.counter += 1
        return {"identity": deepcopy(self.identity), "expectedGeneration": self.grant["context"]["nativeGeneration"],
            "leaseId": self.grant["leaseId"], "attempt": {"controllerSessionId": self.owner,
                "actionId": "typed-clock-" + str(self.counter), "attemptId": "1"}}

    async def acquire(self, label):
        status = outcome(await self.wire(label + "-status", "authority_read_status", {"identity": self.identity}), "status")
        self.grant = outcome(await self.wire(label, "authority_control", {"acquire": {
            "identity": self.identity, "expectedGeneration": status["context"]["nativeGeneration"],
            "owner": {"controllerSessionId": self.owner, "playerDirection": str(self.counter + 1)}, "leaseMs": 30000}}), "granted")
        return self.grant

    async def renew_authority(self):
        assert self.grant is not None
        self.grant = outcome(await self.wire("authority-renew", "authority_control", {"renew": {
            "identity": self.identity, "expectedGeneration": self.grant["context"]["nativeGeneration"],
            "controllerSessionId": self.owner, "leaseId": self.grant["leaseId"], "leaseMs": 30000}}), "granted")

    async def control(self, method, request):
        receipt = outcome(await self.wire(method, "clock_" + method, request), "receipt")
        assert receipt["attempt"] == request["authority"]["attempt"], receipt
        assert receipt["admittedContext"]["identity"] == self.identity, receipt
        assert receipt["authorizingOwner"]["controllerSessionId"] == self.owner, receipt
        status = outcome({key: receipt[key] for key in ("applied", "uncertain") if key in receipt}, "applied")["status"]
        projected = project_status(status)
        assert projected["owner"] == self.owner and status["context"]["identity"] == self.identity, receipt
        self.controls.append({"method": method, "request": deepcopy(request), "receipt": deepcopy(receipt)})
        self.report["controls"] = self.controls
        self.epoch = projected["epoch"]
        return projected

    async def change(self, speed, *, max_ticks, **policy_arguments):
        assert not policy_arguments, "This acceptance adapter uses the explicit colony policy only"
        assert speed in ("Normal", "Fast", "Superfast") and type(max_ticks) is int and max_ticks > 0
        assert self.hold is None, f"External clock hold: {self.hold}"
        before = outcome(await self.wire("clock-before-start", "clock_read_status", {"identity": self.identity}), "status")
        assert before["context"]["identity"] == self.identity, before
        if "newestCursor" in before:
            watermark = integer(before["newestCursor"])
        else:
            assert "neverStarted" in before and not self.controls, before
            watermark = 0  # Fresh profile: no owned epoch has created a journal.
        status = await self.control("start", {"authority": self.precondition(), "speed": "SPEED_" + speed.upper(),
            "policy": deepcopy(POLICY), "leaseMs": 30000, "maxTicks": max_ticks})
        assert status["nativeTickBoundary"] is True and status["tickDeadline"] - status["startTick"] == max_ticks, status
        if self.on_started is not None:
            await self.on_started(status)
        # advance_game needs the exclusive pre-dispatch event watermark, including
        # stops produced by Start's initial probe. The native reply remains intact.
        status["newestCursor"] = watermark
        return status

    async def call(self, *, op, **arguments):
        if op == "events":
            after = arguments["afterCursor"]
            page = outcome(await self.wire("clock-events", "clock_read_events", {"identity": self.identity,
                "afterCursor": str(after), "limit": arguments["limit"]}), "page")
            return project_events(page, after)
        if op == "status":
            status = outcome(await self.wire("clock-status", "clock_read_status", {"identity": self.identity}), "status")
        else:
            assert op == "pause" and arguments["owner"] == self.owner
            status = outcome(await self.wire("clock-pause", "clock_pause", {"identity": self.identity,
                "owner": {"controllerSessionId": arguments["owner"], "epoch": str(arguments["epoch"])}}), "status")
        assert status["context"]["identity"] == self.identity, status
        projected = project_status(status)
        if projected.get("stopReason") in HOLD_REASONS:
            self.hold = projected["stopReason"]
        return projected

    async def poll(self):
        batch = await self.call(op="events", afterCursor=self.cursor, limit=128)
        self.cursor = batch["nextCursor"]
        return batch["events"]


class ScenarioRuntime:
    def __init__(self, bridge, supervisor, report):
        self.game, self.supervisor, self.report = BridgeGame(bridge), supervisor, report
        self.lock, self.review_task, self.clock_events = asyncio.Lock(), None, []

    def receive_clock_events(self):
        self.report.setdefault("delivered_events", []).extend(self.clock_events)
        self.clock_events.clear()

    def note(self, kind, message, **details):
        self.report.setdefault("notes", []).append(dict(kind=kind, message=message, **details))


async def run(root: Path, output: Path, *, headless=True, timeout_seconds=900):
    output.mkdir(parents=True, exist_ok=False)
    evidence, report = Evidence(output), {"passed": False, "scope": "Typed native clock tick budgets, owned controls, replay, durable events and revocation."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, tool, arguments=None):
                return payload(await evidence.call(bridge, label, tool, arguments))

            async def wire(label, tool, request):
                return proto(await call(label, "rimgovernor/" + tool, {"request": json.dumps(request, ensure_ascii=False)}))

            try:
                async with asyncio.timeout(timeout_seconds):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    assert CLOCK_TOOLS <= set(names), f"Missing typed clock tools: {CLOCK_TOOLS - set(names)}"
                    report["discovery"] = names
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    loaded = outcome(await wire("identity", "lifecycle_read_identity", {}), "loaded")
                    assert loaded["paused"] is True
                    identity = loaded["context"]["identity"]
                    initial = outcome(await wire("never-started", "clock_read_status", {"identity": identity}), "status")
                    assert "neverStarted" in initial and initial["actualPaused"] is True, initial
                    supervisor = TypedScenarioClock(wire, identity, "native-typed-clock-acceptance", report)
                    runtime = ScenarioRuntime(bridge, supervisor, report)
                    await supervisor.acquire("acquire")
                    malformed = {"authority": supervisor.precondition(), "speed": "SPEED_SUPERFAST", "leaseMs": 30000, "maxTicks": 60}
                    invalid = outcome(await wire("missing-policy", "clock_start", malformed), "failure")
                    assert invalid["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    assert "unknown" in await wire("unadmitted", "clock_read_attempt", {"identity": identity, "attempt": malformed["authority"]["attempt"]})
                    budget = await advance_game(runtime, 120, report, timeout=180)
                    assert budget["stopReason"] == "tick_budget" and budget["pauseVerified"] is True
                    await supervisor.renew_authority()
                    started = await supervisor.change("Normal", max_ticks=1800)
                    assert started["active"] is True, started
                    owned = {"identity": identity, "owner": {"controllerSessionId": supervisor.owner, "epoch": str(started["epoch"])}}
                    renewed = await supervisor.control("renew", {"epoch": owned, "authority": supervisor.precondition(), "leaseMs": 30000})
                    assert renewed["epoch"] == started["epoch"] and renewed["tickDeadline"] == started["tickDeadline"]
                    sped = await supervisor.control("change_speed", {"epoch": owned, "authority": supervisor.precondition(), "speed": "SPEED_FAST"})
                    assert sped["epoch"] == started["epoch"] and sped["tickDeadline"] == started["tickDeadline"]
                    paused = await supervisor.call(op="pause", owner=supervisor.owner, epoch=started["epoch"])
                    assert paused["active"] is False and paused["paused"] is True and paused["pauseVerified"] is True, paused
                    assert paused["stopReason"] == "requested_pause", paused

                    # Exact original receipts survive stop/revocation; current status
                    # is separately observed and never substituted into old receipts.
                    for record in supervisor.controls:
                        replay = outcome(await wire("replay-" + record["method"], "clock_" + record["method"], record["request"]), "receipt")
                        assert replay == record["receipt"]
                        lookup = outcome(await wire("lookup-" + record["method"], "clock_read_attempt", {
                            "identity": identity, "attempt": record["request"]["authority"]["attempt"]}), "receipt")
                        assert lookup == replay
                    first = supervisor.controls[0]
                    changed = deepcopy(first["request"]); changed["maxTicks"] += 1
                    assert outcome(await wire("attempt-conflict", "clock_start", changed), "failure")["code"] == "FAILURE_CODE_ATTEMPT_CONFLICT"
                    cross = {"precondition": first["request"]["authority"], "operation": {"placeBuilding": {}}}
                    assert outcome(await wire("cross-family-execute", "operations_execute", cross), "failure")["code"] == "FAILURE_CODE_ATTEMPT_CONFLICT"
                    assert outcome(await wire("cross-family-lookup", "receipts_lookup", {"identity": identity,
                        "attempt": first["receipt"]["attempt"]}), "failure")["code"] == "FAILURE_CODE_ATTEMPT_CONFLICT"
                    wrong_owner = deepcopy(owned); wrong_owner["owner"]["epoch"] = str(started["epoch"] + 1)
                    assert outcome(await wire("wrong-owner-pause", "clock_pause", wrong_owner), "failure")["code"] == "FAILURE_CODE_OWNER_CONFLICT"

                    history, cursor = [], 0
                    while True:
                        page = await supervisor.call(op="events", afterCursor=cursor, limit=3)
                        history.extend(row["native"] for row in page["events"])
                        cursor = page["nextCursor"]
                        if cursor == integer(page["native"]["newestCursor"]):
                            break
                    assert history and all(row["context"]["identity"] == identity for row in history)
                    report["history_before_revocation"] = history
                    assert outcome(await wire("future-cursor", "clock_read_events", {"identity": identity,
                        "afterCursor": str(cursor + 1), "limit": 1}), "failure")["code"] == "FAILURE_CODE_INVALID_REQUEST"

                    async def revoke_after_start(state):
                        assert state["active"] is True, state
                        authority = outcome(await wire("pre-revoke-authority", "authority_read_status", {"identity": identity}), "status")
                        assert "active" in authority
                        report["revoked"] = outcome(await wire("revoke-running", "authority_control", {"revoke": {
                            "identity": identity, "expectedGeneration": authority["context"]["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL"}}), "revoked")

                    await supervisor.renew_authority()
                    supervisor.on_started = revoke_after_start
                    try:
                        await advance_game(runtime, 18000, report, timeout=60, expected_letters=())
                    except ScenarioInterrupted:
                        stopped = await supervisor.call(op="status")
                        assert stopped["stopReason"] == "external_pause" and stopped["active"] is False, stopped
                        assert stopped["paused"] is True and stopped["pauseVerified"] is True, stopped
                        report["expected_revocation_interruption"] = stopped
                    else:
                        raise AssertionError("Authority revocation did not interrupt bounded native play")
                    await supervisor.call(op="pause", owner=supervisor.owner, epoch=supervisor.epoch)
                    record = supervisor.controls[-1]
                    assert outcome(await wire("replay-after-revoke", "clock_start", record["request"]), "receipt") == record["receipt"]
                    assert outcome(await wire("lookup-after-revoke", "clock_read_attempt", {"identity": identity,
                        "attempt": record["receipt"]["attempt"]}), "receipt") == record["receipt"]
                    stale = deepcopy(record["request"]); stale["authority"] = supervisor.precondition()
                    assert outcome(await wire("new-start-after-revoke", "clock_start", stale), "failure")["code"] == "FAILURE_CODE_STALE_GENERATION"
                    fresh_history = outcome(await wire("retained-history", "clock_read_events", {"identity": identity,
                        "afterCursor": "0", "limit": 128}), "page")
                    checked = project_events(fresh_history, 0)
                    assert [row["native"] for row in checked["events"]][:len(history)] == history
                    assert any(row.get("kind") == "external_pause" for row in checked["events"])
                    report["history_after_revocation"] = fresh_history
                    final = outcome(await wire("final-identity", "lifecycle_read_identity", {}), "loaded")
                    assert final["context"]["identity"] == identity and final["paused"] is True
                    assert integer(final["context"]["tick"]) > integer(loaded["context"]["tick"])
                    report["final"] = final
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report["error"] = repr(error)
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
    parser.add_argument("--timeout-seconds", type=int, default=900)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-typed-clock-acceptance",
        headless=not args.rendered, timeout_seconds=args.timeout_seconds)) else 1)

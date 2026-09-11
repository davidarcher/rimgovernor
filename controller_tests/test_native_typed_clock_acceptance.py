import asyncio
from copy import deepcopy
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from native_typed_clock_acceptance import project_events, project_status, TypedScenarioClock
from rimgovernor.native_scenario import advance_game, ScenarioInterrupted


IDENTITY = {"colonyId": "colony", "loadToken": "load", "mapId": 0}
CONTEXT = {"identity": IDENTITY, "tick": "10", "nativeGeneration": "3"}
OWNER = {"controllerSessionId": "test", "epoch": "1"}


def status(reason="STOP_REASON_TICK_BUDGET", cursor=2, tick=20, epoch=1):
    return {"context": dict(CONTEXT, tick=str(tick)), "nativeTickBoundary": True,
        "actualPaused": True, "observedSpeed": "OBSERVED_SPEED_PAUSED", "newestCursor": str(cursor),
        "stopped": {"reason": reason, "pauseVerified": True, "actualPaused": True,
            "epoch": {"owner": dict(OWNER, epoch=str(epoch)), "origin": CONTEXT,
                "startTick": "10", "lastTick": str(tick), "tickDeadline": "20"}}}


def event(cursor=1, reason="STOP_REASON_LETTER_PAUSE"):
    return {"cursor": str(cursor), "context": deepcopy(CONTEXT), "owner": dict(OWNER),
        "stopped": {"reason": reason, "pause": {"letter": {"id": "Letter1"}}}}


def page(events=None, *, newest=1, next_cursor=1):
    return {"context": dict(CONTEXT, tick="30", nativeGeneration="99"), "events": events if events is not None else [event()],
        "oldestCursor": "1", "newestCursor": str(newest), "nextCursor": str(next_cursor), "gap": False, "lostCount": "0"}


def test_event_projection_preserves_original_context_and_exact_letter():
    original = page()
    translated = project_events(original, 0)
    row = translated["events"][0]
    assert row["native"]["context"] == CONTEXT
    assert row["native"]["context"] != original["context"]
    assert row["event"] == {"letterId": "Letter1", "source": "LetterStack.ReceiveLetter"}
    original["events"][0]["context"]["tick"] = "999"
    assert row["native"]["context"]["tick"] == "10"


@pytest.mark.parametrize("mutation", [
    lambda p: p.update(gap=True), lambda p: p.update(lostCount="1"),
    lambda p: p.update(nextCursor="0"), lambda p: p.update(events=[]),
    lambda p: p["events"][0].update(cursor="2"),
    lambda p: p["events"][0]["stopped"]["pause"].pop("letter"),
    lambda p: p["events"][0].pop("context"),
    lambda p: p["events"][0]["owner"].update(epoch="0"),
])
def test_missing_or_incomplete_event_evidence_fails(mutation):
    value = page(); mutation(value)
    with pytest.raises((AssertionError, KeyError)):
        project_events(value, 0)


def test_stopped_does_not_infer_verified_pause():
    value = status(); value["stopped"]["pauseVerified"] = False
    assert project_status(value)["pauseVerified"] is False
    value["actualPaused"] = False; value["stopped"]["actualPaused"] = False
    assert project_status(value)["paused"] is False
    value["stopped"].pop("pauseVerified")
    with pytest.raises(AssertionError):
        project_status(value)


def test_page_does_not_invent_missing_retention_metadata():
    value = page(events=[], newest=2, next_cursor=2)
    value.pop("oldestCursor")
    assert "oldestCursor" not in project_events(value, 2)["native"]


def test_legacy_unavailable_status_is_not_an_owned_epoch():
    with pytest.raises(AssertionError):
        project_status({"context": CONTEXT, "unavailable": {"reason": "UNAVAILABLE_REASON_NOT_OBSERVED"}})


async def test_initial_probe_letter_is_inspected_before_remaining_ticks_resume():
    calls, events, report = [], [], {}
    native = {"context": deepcopy(CONTEXT), "neverStarted": {}, "actualPaused": True}
    starts = 0

    async def wire(label, tool, request):
        nonlocal native, starts
        calls.append((tool, deepcopy(request)))
        if tool == "clock_read_status":
            return {"status": deepcopy(native)}
        if tool == "clock_read_events":
            after = int(request["afterCursor"])
            selected = [e for e in events if int(e["cursor"]) > after][:request["limit"]]
            return {"page": page(deepcopy(selected), newest=len(events), next_cursor=int(selected[-1]["cursor"]) if selected else after)}
        assert tool == "clock_start"
        starts += 1
        reason = "STOP_REASON_LETTER_PAUSE" if starts == 1 else "STOP_REASON_TICK_BUDGET"
        native = status(reason, starts, 10 if starts == 1 else 20, starts)
        events.append(event(starts, reason))
        events[-1]["owner"]["epoch"] = str(starts)
        return {"receipt": {"attempt": request["authority"]["attempt"], "admittedContext": deepcopy(CONTEXT),
            "authorizingOwner": {"controllerSessionId": "test", "playerDirection": "1"}, "applied": {"status": deepcopy(native)}}}

    supervisor = TypedScenarioClock(wire, IDENTITY, "test", report)
    supervisor.grant = {"context": CONTEXT, "leaseId": "lease"}

    async def query(tool, **kwargs):
        if tool == "home/colony_identity": return dict(IDENTITY)
        assert tool == "home/status"
        return {"skipped": [], "blocks": {"colonists": True, "threats": True},
            "letters": [{"id": "Letter1", "label": "Ancient danger", "letterDef": "ThreatBig"}],
            "ui": {"modalOpen": False}, "time": {"paused": True},
            "counts": {"hostileCount": 0, "huntingPredatorCount": 0, "downedCount": 0},
            "colonists": [{"dead": False, "bleeding": False, "downed": False}]}

    runtime = SimpleNamespace(supervisor=supervisor, game=SimpleNamespace(query=query), review_task=None,
        lock=asyncio.Lock(), clock_events=[], receive_clock_events=lambda: None, note=lambda *args, **kwargs: None)
    result = await advance_game(runtime, 10, report)
    assert result["lastTick"] == 20 and starts == 2
    assert report["simulation"][0]["interruptions"][0]["acknowledgedLetterId"] == "Letter1"
    assert [request["maxTicks"] for tool, request in calls if tool == "clock_start"] == [10, 10]
    assert all(tool.startswith("clock_") for tool, _ in calls)


async def test_external_stop_is_retained_and_never_resumed():
    native = status("STOP_REASON_EXTERNAL_PAUSE", 1, 10)
    starts = 0

    async def wire(label, tool, request):
        nonlocal starts
        if tool == "clock_read_status":
            return {"status": deepcopy(native) if starts else {"context": CONTEXT, "neverStarted": {}}}
        assert tool == "clock_start"
        starts += 1
        return {"receipt": {"attempt": request["authority"]["attempt"], "admittedContext": CONTEXT,
            "authorizingOwner": {"controllerSessionId": "test"}, "applied": {"status": native}}}

    async def query(tool, **kwargs): return dict(IDENTITY)
    supervisor = TypedScenarioClock(wire, IDENTITY, "test", {})
    supervisor.grant = {"context": CONTEXT, "leaseId": "lease"}
    runtime = SimpleNamespace(supervisor=supervisor, game=SimpleNamespace(query=query), review_task=None,
        lock=asyncio.Lock(), clock_events=[], receive_clock_events=lambda: None)
    report = {}
    with pytest.raises(ScenarioInterrupted):
        await advance_game(runtime, 10, report, expected_letters=())
    assert starts == 1 and supervisor.hold == "external_pause"
    assert report["simulation"][0]["failure"] == "Unexpected native interruption"

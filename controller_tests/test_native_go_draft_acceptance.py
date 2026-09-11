"""Pure draft acceptance evidence checks; no game, listener or subprocess."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("go_draft_acceptance", SCRIPTS / "native_go_draft_acceptance.py")
    probe = importlib.util.module_from_spec(spec); spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)


def plan(mode="ordinary"):
    submission = dict(planId="plan", revision="1", actionId="action", draft={"pawnId": "pawn"})
    value = dict(id="plan", revision="1", actions=[dict(id="action", kind="owned_draft", draft={"pawnId": "pawn"}, progress={
        "attempt": "1", "unresolved": False, "stage": "completed", "effect": "completed",
        "draftCleanup": {"stage": "superseded" if mode == "override" else "released"}})])
    return value, submission


@pytest.mark.parametrize("mode", probe.MODES)
def test_terminal_requires_separate_cleanup(mode):
    value, submission = plan(mode); probe.verify_terminal(value, submission, mode)
    value["actions"][0]["progress"]["draftCleanup"]["stage"] = "required"
    with pytest.raises(AssertionError): probe.verify_terminal(value, submission, mode)


@pytest.mark.parametrize("fault", ["attempt", "unresolved", "kind", "pawn", "secret", "building", "revision"])
def test_terminal_rejects_false_evidence(fault):
    value, submission = plan(); action = value["actions"][0]
    if fault == "attempt": action["progress"]["attempt"] = "2"
    elif fault == "unresolved": action["progress"]["unresolved"] = True
    elif fault == "kind": action["kind"] = "building"
    elif fault == "pawn": action["draft"]["pawnId"] = "other"
    elif fault == "secret": action["progress"]["claimId"] = "private"
    elif fault == "building": action["building"] = {}
    else: value["revision"] = "2"
    with pytest.raises(AssertionError): probe.verify_terminal(value, submission, "ordinary")


def events(mode="ordinary"):
    names = ["rimgovernor/lifecycle_read_identity"]
    if mode != "restart": names += [probe.CONTROL, probe.EXECUTE]
    if mode == "override": names += [probe.FIXTURE, probe.FIXTURE]
    if mode in {"ordinary", "lost_execute"}: names += [probe.RELEASE]
    if mode != "restart": names += [probe.CONTROL]
    rows = []
    for i, name in enumerate(names):
        request = {}
        if name == probe.CONTROL: request = {"acquire" if i == 1 else "revoke": {}}
        if name == probe.EXECUTE: request = {"operation": {"setDrafted": {"pawn": {"entityId": "pawn"}, "drafted": True, "allowPersistentDraft": False}}}
        if name == probe.RELEASE: request = {"pawn": {"entityId": "pawn"}, "expectedClaimId": "claim", "originalOwner": {"session": "owner"}}
        args = {"operation": "draft", "pawnId": "pawn"} if name == probe.FIXTURE else {"request": json.dumps(request)}
        rows.append(dict(Sequence=i+11, OperationId=str(i), CapabilityId=name, EventType="operation.completed", Success=True, HasResult=True, Metadata={"arguments": args}))
    return rows, {name: {name} for name in names}


@pytest.mark.parametrize("mode", (*probe.MODES, "restart"))
def test_sdk_trace_counts_exact_execute_and_override(mode):
    rows, caps = events(mode); probe.audit(rows, 10, caps, mode, "pawn")


@pytest.mark.parametrize("fault", ["gap", "duplicate", "unknown", "execute_twice", "acquire_twice", "wrong_pawn", "persistent", "no_result", "unfinished"])
def test_sdk_trace_rejects_false_proof(fault):
    rows, caps = events()
    if fault == "gap": rows.pop(0)
    elif fault == "duplicate": rows.append(copy.deepcopy(rows[-1]))
    elif fault == "unknown": rows[0]["CapabilityId"] = "rimworld/spawn_thing"
    elif fault in {"execute_twice", "acquire_twice"}:
        row = copy.deepcopy(rows[2 if fault == "execute_twice" else 1]); row["Sequence"] = rows[-1]["Sequence"]+1; row["OperationId"] = "extra"; rows.append(row)
    elif fault in {"wrong_pawn", "persistent"}:
        request = json.loads(rows[2]["Metadata"]["arguments"]["request"])
        if fault == "wrong_pawn": request["operation"]["setDrafted"]["pawn"]["entityId"] = "other"
        else: request["operation"]["setDrafted"]["allowPersistentDraft"] = True
        rows[2]["Metadata"]["arguments"]["request"] = json.dumps(request)
    elif fault == "no_result": rows[2]["HasResult"] = False
    else: rows[2]["EventType"] = "operation.started"
    with pytest.raises(AssertionError): probe.audit(rows, 10, caps, "ordinary", "pawn")


def test_current_diagnostic_can_be_inflight():
    rows, caps = events(); name = "rimbridge/list_operation_events"; caps[name] = {name}
    rows.append(dict(Sequence=rows[-1]["Sequence"]+1, OperationId="diagnostic", CapabilityId=name, EventType="operation.started"))
    probe.audit(rows, 10, caps, "ordinary", "pawn")


def response():
    return {"jsonrpc": "2.0", "id": 4, "result": {"content": [], "structuredContent": {"payload": json.dumps({"receipt": {"attempt": {"controllerSessionId": "owner", "actionId": "draft-action", "attemptId": "1"},
        "admittedContext": {"identity": {"colonyId": "colony", "loadToken": "load", "mapId": 0}, "tick": "19", "nativeGeneration": "2"},
        "authorizingOwner": {"controllerSessionId": "owner", "playerDirection": "1"}, "applied": {"observed": {"job": {
        "pawnId": "pawn", "drafted": True, "issued": True, "verified": True, "draftClaimId": "claim", "draftOwner": "owner", "resultingSnapshotToken": "token"}}}}})}, "isError": False}}


def test_claim_requires_actual_verified_native_reply():
    reply = response(); assert probe.issued_claim(reply)["draftClaimId"] == "claim"
    value = json.loads(reply["result"]["structuredContent"]["payload"]); value["receipt"]["applied"]["observed"]["job"]["verified"] = False
    reply["result"]["structuredContent"]["payload"] = json.dumps(value)
    with pytest.raises(AssertionError): probe.issued_claim(reply)


@pytest.mark.parametrize("mode", probe.MODES)
def test_proxy_requires_one_actual_execute_before_fault(tmp_path, mode):
    request = {"id": 4, "method": "tools/call", "params": {"name": "games_call_tool", "arguments": {"tool": probe.EXECUTE}}}
    records = [{"direction": "to-native", "message": request}, {"direction": "from-native", "message": response()}]
    if mode != "override":
        release_request = {"id": 8, "method": "tools/call", "params": {"name": "games_call_tool", "arguments": {"tool": probe.RELEASE}}}
        release_value = {"released": {"request": {"expectedClaimId": "claim", "originalOwner": {"controllerSessionId": "owner"}},
            "observed": {"pawnId": "pawn", "drafted": False, "verified": True, "issued": True}}}
        reply = {"id": 8, "result": {"content": [], "structuredContent": {"payload": json.dumps(release_value)}}}
        records.extend([{"direction": "to-native", "message": release_request}, {"direction": "from-native", "message": reply}])
    if mode != "ordinary": records.append({"direction": "fault", "message": {"mode": mode}})
    path = tmp_path / "wire.jsonl"; path.write_text("\n".join(json.dumps(row) for row in records), encoding="utf8")
    assert probe.proxy_summary(path, mode, "pawn")["native_execute_count"] == 1
    if mode != "override":
        invalid = copy.deepcopy(records)
        release_reply = next(row["message"] for row in invalid if row["direction"] == "from-native" and row["message"]["id"] == 8)
        value = json.loads(release_reply["result"]["structuredContent"]["payload"])
        value["released"]["request"]["expectedClaimId"] = "foreign-claim"
        release_reply["result"]["structuredContent"]["payload"] = json.dumps(value)
        path.write_text("\n".join(json.dumps(row) for row in invalid), encoding="utf8")
        with pytest.raises(AssertionError): probe.proxy_summary(path, mode, "pawn")
    records.append({"direction": "to-native", "message": copy.deepcopy(request)})
    path.write_text("\n".join(json.dumps(row) for row in records), encoding="utf8")
    with pytest.raises(AssertionError): probe.proxy_summary(path, mode, "pawn")


def test_proxy_launcher_has_only_narrow_mode_and_exact_fixture(tmp_path):
    wrapper = probe.proxy_executable(tmp_path / "proxy", Path("/inputs/gabs/gabs"), "override", {"pawnId": "pawn"})
    text = wrapper.read_text(encoding="utf8")
    assert "--proxy-real" in text and "--proxy-mode override" in text and '"$@"' in text
    assert json.loads((wrapper.parent / "fixture.json").read_text(encoding="utf8")) == {"pawnId": "pawn"}


@pytest.mark.parametrize("branch", ["noChange", "uncertain"])
def test_receipt_rejects_competing_outcome(branch):
    reply = response()
    value = json.loads(reply["result"]["structuredContent"]["payload"])
    value["receipt"][branch] = {}
    reply["result"]["structuredContent"]["payload"] = json.dumps(value)
    with pytest.raises(AssertionError): probe.issued_claim(reply)

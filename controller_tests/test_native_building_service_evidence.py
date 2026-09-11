"""Evidence checks use no game process, HTTP listener or network connection."""
import asyncio
import argparse
import hashlib
import copy
import importlib.util
from pathlib import Path
import sys

import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("building_service_evidence", SCRIPTS / "native_building_service_acceptance.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)

CAPS = {key: {value} for key, value in {
    "identity": "rimgovernor/lifecycle_read_identity", "control": probe.CONTROL,
    "execute": probe.EXECUTE, "lookup": "rimgovernor/receipts_lookup",
    "observe": "rimgovernor/receipts_observe_progress", "diagnostic": "rimbridge/list_capabilities"}.items()}


def events(*names):
    return [{"Sequence": index + 11, "OperationId": str(index), "CapabilityId": name}
            for index, name in enumerate(names)]


@pytest.mark.parametrize("rotation", ["north", "east", "south", "west"])
def test_fixture_rotation_maps_exact_http_cardinal(rotation):
    site = dict(defName="Wall", x=1, z=2, stuff="WoodLog", rotation="ROTATION_" + rotation.upper())
    assert probe.http_building(site) == site | {"rotation": rotation}
    assert site["rotation"].startswith("ROTATION_")


@pytest.mark.parametrize("rotation", ["north", "North", "ROTATION_UNSPECIFIED", ""])
def test_unknown_fixture_rotation_is_not_guessed(rotation):
    with pytest.raises(AssertionError):
        probe.http_building({"rotation": rotation})


def test_full_diagnostic_sequence_precedes_operation_grouping():
    rows = events("identity", "control", "execute", "execute", "control", "diagnostic")
    rows[3]["OperationId"] = rows[2]["OperationId"]
    assert probe.audit(list(reversed(rows)), 10, CAPS, "execute").count(probe.EXECUTE) == 1


@pytest.mark.parametrize("phase", ["submit", "restart"])
@pytest.mark.parametrize("write", ["control", "execute"])
def test_read_only_service_phases_reject_hidden_native_mutations(phase, write):
    with pytest.raises(AssertionError):
        probe.audit(events("identity", "lookup", "observe", write), 10, CAPS, phase)


@pytest.mark.parametrize("fault", ["gap", "duplicate", "unmapped", "unattributed", "ambiguous", "changed", "twice", "before", "after"])
def test_incomplete_or_misattributed_execution_cannot_pass(fault):
    rows = events("identity", "control", "execute", "control", "diagnostic")
    caps = copy.deepcopy(CAPS)
    if fault == "gap":
        rows.pop(4)
        rows[-1]["Sequence"] += 1
    elif fault == "duplicate":
        rows.append(dict(rows[-1]))
    elif fault == "unmapped":
        rows[2]["CapabilityId"] = "unknown"
    elif fault == "unattributed":
        rows[2]["CapabilityId"] = None
    elif fault == "ambiguous":
        caps["execute"].add(probe.CONTROL)
    elif fault == "changed":
        rows[2]["OperationId"] = rows[1]["OperationId"]
    elif fault == "twice":
        rows[4]["CapabilityId"] = "execute"
    elif fault == "before":
        rows[1]["CapabilityId"], rows[2]["CapabilityId"] = "execute", "control"
    else:
        rows[2]["CapabilityId"], rows[3]["CapabilityId"] = "control", "execute"
    with pytest.raises(AssertionError):
        probe.audit(rows, 10, caps, "execute")


@pytest.mark.parametrize("missing", ["identity", "lookup", "observe"])
def test_restart_requires_actual_lookup_and_progress_native_reads(missing):
    with pytest.raises(AssertionError):
        probe.audit(events(*(name for name in ("identity", "lookup", "observe") if name != missing)), 10, CAPS, "restart")


def test_restart_complete_evidence_passes():
    assert len(probe.audit(events("identity", "lookup", "observe"), 10, CAPS, "restart")) == 3


def sample(completed=False):
    submission = dict(planId="plan", actionId="action", revision="1", building={"defName": "Wall"})
    progress = dict(stage="completed" if completed else "awaiting_observation", attempt="1", receipt="accepted",
                    unresolved=not completed, effect="completed" if completed else None)
    plan = dict(id="plan", revision="1", actions=[dict(id="action", building={"defName": "Wall"}, progress=progress)])
    return plan, submission


def test_admission_is_not_pawn_completion():
    plan, submission = sample()
    probe.verify_progress(plan, submission, False)
    with pytest.raises(AssertionError):
        probe.verify_progress(plan, submission, True)
    plan, submission = sample(True)
    probe.verify_progress(plan, submission, True)


@pytest.mark.parametrize("fault", ["plan", "action", "revision", "building", "attempt", "receipt", "effect", "unresolved"])
def test_completion_requires_original_correlated_action_and_evidence(fault):
    plan, submission = sample(True)
    if fault == "plan":
        plan["id"] = "other"
    elif fault == "action":
        plan["actions"][0]["id"] = "other"
    elif fault == "revision":
        plan["revision"] = "2"
    elif fault == "building":
        plan["actions"][0]["building"] = {}
    else:
        plan["actions"][0]["progress"][fault] = True if fault == "unresolved" else "wrong"
    with pytest.raises(AssertionError):
        probe.verify_progress(plan, submission, True)


def test_service_argv_keeps_same_database_without_takeover(tmp_path):
    argv = probe.service_argv(*(tmp_path / name for name in ("binary", "gabs", "config", "profile", "state")))
    assert argv[argv.index("--state") + 1] == str((tmp_path / "state").resolve())
    assert argv[argv.index("--profile") + 1] == str((tmp_path / "profile").resolve())
    assert "--player-control" in argv and argv[argv.index("--listen") + 1] == "127.0.0.1:0"
    assert not any("takeover" in argument.lower() for argument in argv)


class Process:
    returncode = None

    def __init__(self, *, hangs=False, code=0):
        self.hangs, self.code, self.signals = hangs, code, []

    def terminate(self):
        self.signals.append("SIGTERM")

    async def wait(self):
        if self.hangs:
            await asyncio.Future()
        self.returncode = self.code

    def kill(self):
        raise AssertionError("Force kill must never enable an overlapping attachment")


def test_service_exit_is_joined_before_handoff():
    process, record = Process(), {}
    asyncio.run(probe.joined_shutdown(process, record))
    assert process.signals == ["SIGTERM"] and record == dict(joined=True, exit_code=0)


def test_failed_drain_retains_resources_without_force_kill():
    process, record = Process(hangs=True), {}
    with pytest.raises(probe.DrainIncomplete):
        asyncio.run(probe.joined_shutdown(process, record, timeout=.001))
    assert process.signals == ["SIGTERM"] and record == dict(joined=False, resources_retained=True)


def test_nonzero_service_exit_cannot_pass():
    with pytest.raises(AssertionError):
        asyncio.run(probe.joined_shutdown(Process(code=1), {}))


def test_explicit_handoff_verifies_copied_binary(tmp_path):
    binary = tmp_path / "private-go"
    binary.write_bytes(b"exact supplied executable")
    digest = hashlib.sha256(binary.read_bytes()).hexdigest()
    assert probe.verify_go_binary(binary, go_source="a" * 40, go_sha256=digest) == digest
    binary.write_bytes(b"different executable")
    with pytest.raises(ValueError, match="differs"):
        probe.verify_go_binary(binary, go_source="a" * 40, go_sha256=digest)


@pytest.mark.parametrize("source,digest", [("", "0" * 64), ("a" * 39, "0" * 64), ("A" * 40, "0" * 64),
    ("a" * 40, "B" * 64), ("a" * 40, "0" * 63), ("a" * 40, "0" * 64 + "\n")])
def test_handoff_rejects_noncanonical_input_before_any_run(tmp_path, source, digest):
    with pytest.raises(ValueError):
        asyncio.run(probe.run(tmp_path, tmp_path / "out", tmp_path / "binary", go_source=source, go_sha256=digest))
    assert not (tmp_path / "out").exists()


def test_handoff_cli_requires_both_values():
    parser = argparse.ArgumentParser()
    probe.add_handoff_arguments(parser)
    for args in ([], ["--go-source", "a" * 40], ["--go-sha256", "b" * 64], ["--go-source", "A" * 40, "--go-sha256", "b" * 64]):
        with pytest.raises(SystemExit):
            parser.parse_args(args)
    args = parser.parse_args(["--go-source", "a" * 40, "--go-sha256", "b" * 64])
    assert args.go_source == "a" * 40 and args.go_sha256 == "b" * 64


def test_player_bootstrap_token_redaction_preserves_live_response():
    value = {"token": "private process token", "mode": "explicit-player"}
    assert probe.redact_http_value("/api/player/session", value) == {"token": "[redacted]", "mode": "explicit-player"}
    assert value["token"] == "private process token"
    assert probe.redact_http_value("/api/player/control", {"state": {"enabled": False}}) == {"state": {"enabled": False}}

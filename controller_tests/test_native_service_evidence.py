"""Native service acceptance must reject missing trace evidence or a hidden write."""
import importlib.util
from pathlib import Path
import sys

import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("native_service_evidence", SCRIPTS / "native_go_service_acceptance.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)


def rows():
    return [{"Sequence": i + 11, "OperationId": str(i), "CapabilityId": kind}
            for i, kind in enumerate(("identity", "status", "identity", "identity", "status", "identity"))]


CAPABILITIES = {"identity": {"rimgovernor/lifecycle_read_identity"},
                "status": {"rimgovernor/observations_read_status"}, "write": {"rimgovernor/operations_execute"}}


def test_native_service_requires_two_attributed_observations():
    assert len(probe.trace(list(reversed(rows())), 10, CAPABILITIES)) == 6


@pytest.mark.parametrize("fault", ["gap", "duplicate", "write", "missing_capability", "missing_status"])
def test_incomplete_or_mutating_service_trace_cannot_pass(fault):
    events = rows()
    if fault == "gap":
        events.pop(2)
    elif fault == "duplicate":
        events.append(dict(events[-1]))
    elif fault == "write":
        events[2]["CapabilityId"] = "write"
    elif fault == "missing_capability":
        events[2]["CapabilityId"] = ""
    else:
        events[1]["CapabilityId"] = "identity"
    with pytest.raises(AssertionError):
        probe.trace(events, 10, CAPABILITIES)

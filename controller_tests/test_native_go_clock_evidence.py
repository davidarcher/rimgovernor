"""Acceptance evidence must distinguish Go orchestration from protocol receipts."""
import copy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
try:
    import native_go_clock_acceptance as probe
finally:
    sys.path.pop(0)


def trace(*names):
    caps = {name: {"rimgovernor/" + name} for name in names}
    rows = [{"Sequence": i + 1, "OperationId": str(i), "CapabilityId": name} for i, name in enumerate(names)]
    return rows, caps


@pytest.mark.parametrize("name", ["clock_start", "clock_renew", "operations_execute", "authority_control"])
def test_disabled_restart_cannot_hide_writes(name):
    rows, caps = trace("clock_read_events", name)
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=True)


def test_go_clock_trace_requires_contiguous_attributed_control():
    rows, caps = trace("clock_read_events", "clock_start", "operations_execute", "clock_pause")
    assert len(probe.audit(rows, 0, caps, restart=False)) == 4
    for bad in (rows[1:], rows + [rows[-1]]):
        with pytest.raises(AssertionError):
            probe.audit(bad, 0, caps, restart=False)
    caps["clock_start"] = {"home/supervised_play"}
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=False)


def test_interruption_requires_exact_native_letter_stop():
    event = {"stopped": {"reason": "STOP_REASON_LETTER_PAUSE", "pause": {"letter": {"id": "Letter_1"}}}}
    assert probe.interrupted_by_letter([event], "Letter_1") == event
    changed = copy.deepcopy(event)
    changed["stopped"]["reason"] = "STOP_REASON_REQUESTED_PAUSE"
    for values in ([], [changed], [event, event]):
        with pytest.raises(AssertionError):
            probe.interrupted_by_letter(values, "Letter_1")
    with pytest.raises(AssertionError):
        probe.interrupted_by_letter([event], "Letter_2")

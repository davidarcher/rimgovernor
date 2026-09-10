import copy
import importlib.util
from pathlib import Path
import pytest

spec = importlib.util.spec_from_file_location('container_camera_acceptance', Path(__file__).resolve().parents[1]/'scripts/container_camera_acceptance.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def camera():
    return dict(mapId='map-a', mapPosition={'x': 140, 'z': 120}, rootSize=24,
                sizeRange={'min': 11, 'max': 60}, cameraZoomExtensionEnabled=False)


@pytest.mark.parametrize('action, axis, delta', [('left', 'x', -10), ('right', 'x', 10), ('up', 'z', 10), ('down', 'z', -10)])
def test_pan_requires_observed_direction_and_distance(action, axis, delta):
    before = camera(); after = copy.deepcopy(before)
    after['mapPosition'][axis] += delta
    probe.verify_pan(before, after, action)
    with pytest.raises(AssertionError):
        probe.verify_pan(before, before, action)
    after['mapPosition'][axis] -= 2*delta
    with pytest.raises(AssertionError):
        probe.verify_pan(before, after, action)


@pytest.mark.parametrize('action, start, end', [('in', 24, 22), ('in', 12, 11), ('in', 11, 11), ('out', 59, 60), ('out', 60, 60)])
def test_zoom_checks_native_range_and_clamping(action, start, end):
    before = camera(); before['rootSize'] = start
    after = copy.deepcopy(before); after['rootSize'] = end
    probe.verify_zoom(before, after, action)
    after['rootSize'] += 1
    with pytest.raises(AssertionError):
        probe.verify_zoom(before, after, action)



def test_owner_heartbeat_renews_during_foreground_work_and_drains():
    import threading
    renewed = threading.Event()
    calls = []
    def api(path, body):
        calls.append((path, body))
        renewed.set()
        return {'active': True}
    keeper = probe.OwnerHeartbeat(api, {'lease_id': 'owner'}, interval=.01)
    try:
        assert renewed.wait(timeout=2)
        keeper.check()
    finally:
        keeper.close()
    assert not keeper.thread.is_alive()
    assert calls and all(path == '/api/input/heartbeat' for path, _ in calls)


def test_failed_owner_heartbeat_is_not_silently_reacquired():
    import threading
    called = threading.Event()
    def api(path, body):
        called.set()
        raise ValueError('expired')
    keeper = probe.OwnerHeartbeat(api, {}, interval=.01)
    assert called.wait(timeout=2)
    keeper.close()
    with pytest.raises(RuntimeError, match='expired'):
        keeper.check()

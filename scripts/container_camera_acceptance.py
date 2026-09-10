"""Native camera/lease acceptance through the same player API as the dashboard."""
import math
import time
import threading
from urllib.error import HTTPError
from urllib.parse import urlencode


class OwnerHeartbeat:
    """Renew during slow foreground reads, then drain before release/expiry."""
    def __init__(self, api, credentials, interval=3):
        self.api, self.credentials, self.interval = api, credentials, interval
        self.stopped = threading.Event()
        self.error = None
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.stopped.wait(self.interval):
            try:
                result = self.api('/api/input/heartbeat', self.credentials)
                if result.get('active') is not True:
                    raise RuntimeError('Owner heartbeat was not acknowledged')
            except Exception as error:
                self.error = error
                return

    def check(self):
        if self.error is not None:
            raise RuntimeError('Background owner heartbeat failed: '+str(self.error)) from self.error

    def close(self):
        self.stopped.set()
        self.thread.join(timeout=30)
        if self.thread.is_alive():
            raise TimeoutError('Owner heartbeat did not drain before release/expiry')


def geometry(camera):
    """Ignore operation timestamps; compare observed native geometry only."""
    return {key: camera[key] for key in ('mapId', 'mapPosition', 'rootSize', 'sizeRange')}


def verify_pan(before, after, action):
    axis, amount = {'left': ('x', -10), 'right': ('x', 10),
                    'up': ('z', 10), 'down': ('z', -10)}[action]
    other = 'z' if axis == 'x' else 'x'
    assert before['mapId'] == after['mapId']
    assert math.isclose(after['mapPosition'][axis]-before['mapPosition'][axis], amount, abs_tol=.01)
    assert math.isclose(after['mapPosition'][other], before['mapPosition'][other], abs_tol=.01)
    assert after['rootSize'] == before['rootSize']


def verify_zoom(before, after, action):
    limits = before['sizeRange']
    expected = max(limits['min'], min(limits['max'], before['rootSize']+(-2 if action == 'in' else 2)))
    assert after['mapId'] == before['mapId'] and after['sizeRange'] == limits
    assert math.isclose(after['rootSize'], expected, abs_tol=.001)
    assert after['cameraZoomExtensionEnabled'] is False


def accept_camera(api, capture, session, tick, report, sleep=time.sleep):
    """Mutate only a disposable paused colony; retain partial evidence on failure."""
    report.update(passed=False, operations=[], refusals=[])
    keeper = None
    def read():
        return api('/api/camera/state?'+urlencode({'session_id': session}))['camera']
    def owner(viewer):
        nonlocal keeper
        if keeper is not None:
            keeper.close()
            keeper.check()
        body = {'session_id': session, 'viewer_id': viewer}
        result = api('/api/input/take', body)
        assert result['mode'] == 'manual'
        credentials = dict(body, lease_id=result['lease_id'])
        keeper = OwnerHeartbeat(api, credentials)
        return credentials
    def heartbeat(credentials):
        api('/api/video', {'viewer': 'container-acceptance', 'playing': True})
        keeper.check()
    def stop_heartbeat():
        nonlocal keeper
        keeper.close()
        keeper.check()
        keeper = None
    def release(credentials):
        stop_heartbeat()
        return api('/api/input/release', dict(credentials, resume=False))
    def owned_capture(credentials, name, previous=None):
        heartbeat(credentials)
        return capture(name, previous, lambda: heartbeat(credentials))
    def paused(credentials=None):
        result = api('/api/time', dict(credentials or {'session_id': session}, speed='Paused'))
        assert result['mode'] == 'manual'
        assert result['game']['paused'] is True and result['game']['ticksGame'] == tick
        return result['game']
    def refused(label, endpoint, body):
        before = read()
        try:
            result = api(endpoint, body)
        except HTTPError as error:
            detail = error.read().decode('utf8')
            report['refusals'].append(dict(label=label, status=error.code, detail=detail))
            assert error.code == 400, detail
        else:
            raise AssertionError(f'{label} was accepted: {result}')
        assert geometry(read()) == geometry(before), label+' changed the native camera'
    def navigate(credentials, action):
        heartbeat(credentials)
        before = read()
        result = api('/api/camera/navigate', dict(credentials, action=action))
        after = read()
        report['operations'].append(dict(action=action, before=before, after=after))
        assert geometry(result['camera']) == geometry(after)
        assert result['following'] is False
        if action in ('in', 'out'):
            verify_zoom(before, after, action)
        else:
            verify_pan(before, after, action)
        return after

    try:
        first = owner('camera-owner-a')
        report['initial_clock'] = paused(first)
        report['initial_camera'] = read()
        report['initial_frame'] = owned_capture(first, 'camera-initial.png')
        for label, endpoint, body in (
            ('other viewer take', '/api/input/take', {'session_id': session, 'viewer_id': 'camera-owner-b'}),
            ('unowned pan', '/api/camera/navigate', {'session_id': session, 'action': 'right'}),
            ('wrong owner pan', '/api/camera/navigate', dict(first, viewer_id='camera-owner-b', action='right')),
            ('wrong token pan', '/api/camera/navigate', dict(first, lease_id='stale', action='right')),
            ('stale session pan', '/api/camera/navigate', dict(first, session_id='stale', action='right')),
            ('unowned play', '/api/time', {'session_id': session, 'speed': 'Normal'}),
        ):
            heartbeat(first)
            refused(label, endpoint, body)
            paused(first)
        for action in ('left', 'right', 'up', 'down'):
            navigate(first, action)
        assert geometry(read()) == geometry(report['initial_camera'])
        report['after_pan_clock'] = paused(first)

        for action, bound in (('in', 'min'), ('out', 'max')):
            # A fixed operation budget prevents schema/range surprises from becoming
            # an unbounded stream of native writes. Each write is observed once.
            for _ in range(64):
                state = navigate(first, action)
                if math.isclose(state['rootSize'], state['sizeRange'][bound], abs_tol=.001):
                    break
            else:
                raise AssertionError('Zoom limit not reached within 64 operations')
            held = navigate(first, action)
            assert geometry(held) == geometry(state), 'Zoom escaped its native limit'
            report[bound+'_clock'] = paused(first)
            previous = report['initial_frame' if bound == 'min' else 'min_frame']['sha256']
            report[bound+'_frame'] = owned_capture(first, 'camera-zoom-'+bound+'.png', previous)
            heartbeat(first)

        release(first)
        refused('released token pan', '/api/camera/navigate', dict(first, action='left'))
        refused('released token play', '/api/time', dict(first, speed='Normal'))
        paused()
        second = owner('camera-owner-b')
        refused('old owner cleanup', '/api/input/release', dict(first, resume=True))
        heartbeat(second)
        report['expiry_wait_seconds'] = 16
        stop_heartbeat()
        sleep(16)  # Actual lease expiry, with no heartbeat or hidden re-acquisition.
        refused('expired owner pan', '/api/camera/navigate', dict(second, action='left'))
        refused('expired heartbeat', '/api/input/heartbeat', second)
        assert api('/api/state')['mode'] == 'manual'
        third = owner('camera-owner-c')
        report['after_expiry_clock'] = paused(third)
        navigate(third, 'left')
        report['after_takeover_frame'] = owned_capture(third, 'camera-takeover.png', report['max_frame']['sha256'])
        heartbeat(third)
        release(third)
        report['final_clock'] = paused()
        report['passed'] = True
    finally:
        if keeper is not None:
            keeper.close()

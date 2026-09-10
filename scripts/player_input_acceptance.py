"""Native player handoff/selection checks using an existing isolated worker API."""
import json
import threading
import urllib.error


def accept(api, worker):
    session = worker['session']
    identity = {'session_id': session, 'viewer_id': 'acceptance-owner'}
    report = {'events': []}
    def call(endpoint, body=None):
        try:
            result = api(worker, endpoint, body)
            report['events'].append({'endpoint': endpoint, 'result': result})
            return result
        except urllib.error.HTTPError as error:
            detail = error.read().decode()
            report['events'].append({'endpoint': endpoint, 'status': error.code, 'detail': detail})
            raise
    stopped = threading.Event()
    thread = None
    try:
        lease = call('/api/input/take', identity)
        owner = dict(identity, lease_id=lease['lease_id'])
        def renew():
            while not stopped.wait(4):
                try:
                    api(worker, '/api/input/heartbeat', owner)
                except Exception as error:
                    report['heartbeat_error'] = repr(error)
                    return
        thread = threading.Thread(target=renew, daemon=True)
        thread.start()
        for endpoint, body in [
            ('/api/input/take', dict(identity, viewer_id='other-viewer')),
            ('/api/control', {'mode': 'automate'}),
            ('/api/input/select', dict(owner, lease_id='stale', pawn_id='')),
        ]:
            call('/api/input/heartbeat', owner)
            try:
                call(endpoint, body)
            except urllib.error.HTTPError as error:
                assert error.code == 400, error
            else:
                raise AssertionError('Unowned input accepted: '+endpoint)
        state = call('/api/state')
        assert state['mode'] == 'manual', state
        pawn = state['observation']['pawns'][0]['thing_id']
        call('/api/input/heartbeat', owner)
        report['selected'] = call('/api/input/select', dict(owner, pawn_id=pawn))
        call('/api/input/heartbeat', owner)
        report['cleared'] = call('/api/input/select', dict(owner, pawn_id=''))
        stopped.set()
        thread.join(timeout=125)
        call('/api/input/heartbeat', owner)
        report['released'] = call('/api/input/release', dict(owner, resume=False))
        assert report['released']['mode'] == 'manual'
        report['paused'] = call('/api/time', {'session_id': session, 'speed': 'Paused'})['game']
        assert report['paused']['paused'] is True
        report['passed'] = True
        return report
    finally:
        stopped.set()
        if thread: thread.join(timeout=125)
        (worker['root']/'player-input.json').write_text(json.dumps(report, indent=2), encoding='utf8')

from copy import deepcopy
from unittest.mock import Mock
import weakref

import httpx
import pytest

from rimbot import scenario_dashboard as dashboard


class Runtime:
    stopped = False
    context_token = 'load-a'
    colony = 'colony-a'
    camera_bytes = b'png-a'
    def __init__(self):
        self.data = {'sessionId': self.context_token, 'mode': 'manual', 'game': {'tick': 42, 'paused': True}}
        self.start = Mock(side_effect=AssertionError('Observer started runtime'))
        self.stop = Mock(side_effect=AssertionError('Observer stopped runtime'))
        self.game = Mock(side_effect=AssertionError('Observer accessed native game'))
    def public(self):
        return self.data


@pytest.mark.asyncio
async def test_readonly_observation_and_session_replacement():
    observer = dashboard.Observer()
    first = Runtime()
    before = deepcopy(first.data)
    observer.runtime = weakref.ref(first)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(observer.app()), base_url='http://testserver') as client:
        assert (await client.get('/')).headers['location'] == '/scenario'
        for _ in range(3):
            assert (await client.get('/api/state')).json()['game']['tick'] == 42
        for method in ('POST', 'PUT', 'PATCH', 'DELETE'):
            for route in ('/api/control', '/api/chat', '/api/video', '/api/session/stop', '/api/player/input'):
                assert (await client.request(method, route, headers={'X-RimBot': '1'}, json={})).status_code == 403
        assert (await client.get('/api/people')).status_code == 404
        assert (await client.get('/api/camera?session_id=load-a')).content == b'png-a'
        second = Runtime()
        second.context_token = 'load-b'
        second.data['sessionId'] = 'load-b'
        observer.runtime = weakref.ref(second)
        assert (await client.get('/api/state')).json()['sessionId'] == 'load-b'
        assert (await client.get('/api/camera?session_id=load-a')).status_code == 409
        second.stopped = True
        assert (await client.get('/api/state')).status_code == 503
    assert first.data == before
    first.start.assert_not_called()
    first.stop.assert_not_called()
    first.game.assert_not_called()


@pytest.mark.asyncio
async def test_hook_is_opt_in_and_reuses_process_observer(monkeypatch):
    starts = Mock()
    monkeypatch.setattr(dashboard.Observer, 'start', starts)
    monkeypatch.delenv('RIMBOT_SCENARIO_DASHBOARD', raising=False)
    first, second = Runtime(), Runtime()
    dashboard.attach(first)
    starts.assert_not_called()
    monkeypatch.setenv('RIMBOT_SCENARIO_DASHBOARD', '1')
    dashboard.attach(first)
    dashboard.attach(first)
    dashboard.attach(second)
    starts.assert_called_once()
    assert next(reversed(list(dashboard._observers.values()))).current() is second

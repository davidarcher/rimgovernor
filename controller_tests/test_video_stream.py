import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from rimbot.bridge_server import create_app
from rimbot.video_stream import CAPACITY, PeerRequest, VideoHub, video_frame


def test_linux_shared_frames_lock_freshness_and_cleanup():
    import sys
    if sys.platform != 'linux':
        pytest.skip('Linux shared-memory protocol')
    import fcntl
    import struct
    import uuid
    from pathlib import Path
    from rimbot.video_stream import RawFrames
    path = Path('/dev/shm') / ('RimBotVideo-' + uuid.uuid4().hex)
    try:
        with path.open('w+b') as writer:
            writer.truncate(CAPACITY)
            writer.write(struct.pack('<Qiid', 1, 1, 1, time.time()))
            writer.seek(40)
            writer.write(b'abc')
            writer.flush()
            reader = RawFrames(str(path))
            try:
                fcntl.flock(writer, fcntl.LOCK_EX)
                assert reader.read() is None
                fcntl.flock(writer, fcntl.LOCK_UN)
                assert reader.read()[4] == b'abc'
                assert reader.read(1) is None
                writer.seek(0)
                writer.write(struct.pack('<Qiid', 2, 1, 1, time.time() - 3))
                writer.flush()
                assert reader.read() is None
                path.unlink()
            finally:
                reader.close()
    finally:
        path.unlink(missing_ok=True)


def test_shared_frames_reject_arbitrary_paths():
    from rimbot.video_stream import RawFrames
    for path in ('/etc/passwd', '/dev/shm/../passwd', '/dev/shm/RimBotVideo-no'):
        with pytest.raises(ValueError):
            RawFrames(path)


def runtime():
    return SimpleNamespace(connected=True, headless=False, context_token='session',
                           video_viewers={}, bridge=SimpleNamespace(call=AsyncMock()))


@pytest.mark.asyncio
@pytest.mark.parametrize('hardware', [False, True])
@pytest.mark.parametrize('replacement', [False, True])
async def test_socket_frame_metadata_acknowledgement_and_owner_disconnect(monkeypatch, hardware, replacement):
    import json
    import struct
    from starlette.websockets import WebSocketDisconnect
    from rimbot.video_stream import socket_frames
    pytest.importorskip('av')
    if hardware:
        from unittest.mock import Mock
        monkeypatch.setattr('rimbot.video_stream.HardwareEncoder.encode', Mock(side_effect=RuntimeError('GPU unavailable')))
    rt = runtime()
    rt.player_input = SimpleNamespace(viewer='a', session='session', token='lease')
    rt.set_mode = AsyncMock()
    hub = VideoHub(rt)
    hub.open_source = AsyncMock()
    hub.source_name = 'native-source'
    hub.latest = (1, 16, 16, time.time(), bytes([255, 0, 0, 255]) * 256, 4)

    class Socket:
        headers = {'origin': 'http://testserver', 'host': 'testserver', 'sec-websocket-protocol': 'rimbot-view-v1'}
        query_params = {'session_id': 'session', 'viewer': 'a', 'connection_id': 'connection', 'hardware': str(hardware).lower()}
        app = SimpleNamespace(state=SimpleNamespace(video=hub))
        accept = AsyncMock()
        close = AsyncMock()
        packets = []

        async def send_bytes(self, packet):
            self.packets.append(packet)

        async def receive_text(self):
            if len(self.packets) == 2:
                raise WebSocketDisconnect()
            hub.latest = (2, 16, 16, hub.latest[3] + .01, hub.latest[4], 4)
            return json.dumps({'frame': 1, 'displayed': time.time()})

    socket = Socket()
    if replacement:
        def replace_owner():
            rt.player_input = SimpleNamespace(viewer='a', session='session', token='new-lease')
        socket.close.side_effect = replace_owner
    await socket_frames(socket)
    assert len(socket.packets) == 2
    for index, packet in enumerate(socket.packets):
        size, = struct.unpack_from('<I', packet)
        metadata = json.loads(packet[4:4 + size])
        assert metadata['frame'] == index + 1 and metadata['source'] == 'native-source'
        assert metadata['width'] == metadata['height'] == 16
        assert metadata['encoding'] == 'jpeg'
        assert packet[4 + size:][:2] == b'\xff\xd8'
    assert not hub.peers
    if replacement:
        rt.set_mode.assert_not_awaited()
    else:
        rt.set_mode.assert_awaited_once_with('manual', player_owner=('session', 'a', 'lease'))


@pytest.mark.asyncio
@pytest.mark.parametrize('origin,protocol,session', [('https://other', 'rimbot-view-v1', 'session'),
    ('http://testserver', '', 'session'), ('http://testserver', 'rimbot-view-v1', 'old-load')])
async def test_socket_rejects_foreign_origin_missing_protocol_and_stale_load(origin, protocol, session):
    from rimbot.video_stream import socket_frames
    rt = runtime(); hub = VideoHub(rt)
    hub.open_source = AsyncMock()
    socket = SimpleNamespace(headers={'origin': origin, 'host': 'testserver', 'sec-websocket-protocol': protocol},
        query_params={'session_id': session, 'viewer': 'a', 'connection_id': 'connection'},
        app=SimpleNamespace(state=SimpleNamespace(video=hub)), close=AsyncMock(), accept=AsyncMock())
    await socket_frames(socket)
    hub.open_source.assert_not_awaited()
    socket.accept.assert_not_awaited()


def test_frame_orientation_and_stride():
    pytest.importorskip('av')
    frame = video_frame((1, 1, 2, 1, bytes([255, 0, 0, 0, 255, 0])))
    data = bytes(frame.planes[0])
    assert data[:3] == bytes([0, 255, 0])
    assert data[frame.planes[0].line_size:][:3] == bytes([255, 0, 0])
    assert frame.pts == 90000


@pytest.mark.asyncio
async def test_expired_viewer_does_not_close_other_viewer():
    rt = runtime()
    hub = VideoHub(rt)
    one, two = SimpleNamespace(close=AsyncMock()), SimpleNamespace(close=AsyncMock())
    from unittest.mock import Mock
    t1, t2 = Mock(), Mock()
    hub.peers = {'one': (one, t1), 'two': (two, t2)}
    await hub.drop('one')
    one.close.assert_awaited_once()
    two.close.assert_not_awaited()
    assert list(hub.peers) == ['two']
    await hub.close()


@pytest.mark.asyncio
async def test_old_close_and_callback_cannot_remove_replacement():
    from unittest.mock import Mock
    rt = runtime()
    hub = VideoHub(rt)
    old, new = SimpleNamespace(close=AsyncMock()), SimpleNamespace(close=AsyncMock())
    track = Mock()
    hub.peers['viewer'] = (new, track)
    hub.peer_ids['viewer'] = ('session', 'new')
    rt.streaming_viewers.add('viewer')
    await hub.drop('viewer', old)
    await hub.disconnect(PeerRequest(viewer='viewer', session_id='session', connection_id='old'))
    await hub.disconnect(PeerRequest(viewer='viewer', session_id='old-session', connection_id='new'))
    assert hub.peers['viewer'][0] is new
    assert 'viewer' in rt.streaming_viewers
    new.close.assert_not_awaited()
    await hub.disconnect(PeerRequest(viewer='viewer', session_id='session', connection_id='new'))
    new.close.assert_awaited_once()
    assert not hub.peers and not rt.streaming_viewers


@pytest.mark.asyncio
async def test_expiry_and_slow_native_renewal_keep_other_viewer_sampling():
    from unittest.mock import Mock
    from collections import deque
    rt = runtime()
    hub = VideoHub(rt)
    entered = asyncio.Event()
    blocked = asyncio.Event()

    async def renew(*args, **kwargs):
        entered.set()
        await blocked.wait()

    rt.bridge.call = renew
    source = SimpleNamespace(read=Mock(side_effect=lambda previous: (previous + 3, 1, 1, time.time(), b'abc')), close=Mock())
    hub.source, hub.session = source, 'session'
    for viewer, until in [('expired', 0), ('live', time.monotonic() + 30)]:
        pc = SimpleNamespace(close=AsyncMock(), connectionState='connected')
        track = SimpleNamespace(stop=Mock(), delivered=0, skipped=0, ages=deque())
        hub.peers[viewer] = (pc, track)
        rt.video_viewers[viewer] = until
    hub.task = asyncio.create_task(hub.run(source, 'session', 'test'))
    try:
        await asyncio.wait_for(entered.wait(), 4)
        before = hub.sampled_frames
        await asyncio.sleep(.15)
        assert hub.sampled_frames > before
        assert list(hub.peers) == ['live']
        assert hub.skipped_frames > 0
        assert hub.status()['viewers'][0]['captureToEncoderMedianMs'] is None
        rt.context_token = 'changed'
        await asyncio.wait_for(hub.task, 1)
        assert not hub.peers and hub.source is None
        source.close.assert_called_once()
    finally:
        await hub.close()


@pytest.mark.asyncio
async def test_close_endpoint_requires_local_header():
    rt = runtime()
    app = create_app(rt)
    app.state.rt = rt
    app.state.video.disconnect = AsyncMock()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        body = {'viewer': 'a', 'session_id': 'session', 'connection_id': 'one'}
        assert (await client.post('/api/video/close', json=body)).status_code == 403
        assert (await client.post('/api/video/close', json=body, headers={'X-RimBot': '1'})).status_code == 200
        assert (await client.get('/api/video/status')).json()['active'] is False
    app.state.video.disconnect.assert_awaited_once()


@pytest.mark.asyncio
async def test_delayed_pause_heartbeat_cannot_override_new_play():
    rt = runtime()
    app = create_app(rt)
    app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimBot': '1'}) as client:
        assert (await client.post('/api/video', json={'viewer': 'a', 'playing': True, 'revision': 3})).status_code == 200
        stale = await client.post('/api/video', json={'viewer': 'a', 'playing': False, 'revision': 2})
        assert stale.json() == {'playing': True, 'ignored': True}
        assert rt.video_viewers['a'] > time.monotonic()
        assert (await client.post('/api/video', json={'viewer': 'a', 'playing': False, 'revision': 4})).json() == {'playing': False}
        assert 'a' not in rt.video_viewers
        assert (await client.post('/api/video', json={'viewer': 'a', 'playing': True, 'revision': True})).status_code == 400

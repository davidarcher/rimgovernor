import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from rimbot.bridge_server import create_app
from rimbot.video_stream import CAPACITY, Offer, PeerRequest, VideoHub, video_frame


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
            writer.seek(32)
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
async def test_signaling_origin_and_session_guards():
    pytest.importorskip('aiortc')
    rt = runtime()
    app = create_app(rt)
    app.state.rt = rt
    body = {'viewer': 'a', 'connection_id': 'test-connection', 'session_id': 'stale', 'sdp': 'm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\n'}
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        assert (await client.post('/api/video/offer', json=body)).status_code == 403
        assert (await client.post('/api/video/offer', json=body, headers={'X-RimBot': '1', 'Origin': 'https://other'})).status_code == 403
        assert (await client.post('/api/video/offer', json=body, headers={'X-RimBot': '1'})).status_code == 400
        body['session_id'] = 'session'
        rt.headless = True
        assert (await client.post('/api/video/offer', json=body, headers={'X-RimBot': '1'})).status_code == 400
    rt.bridge.call.assert_not_awaited()


def test_frame_orientation_and_stride():
    pytest.importorskip('av')
    frame = video_frame((1, 1, 2, 1, bytes([255, 0, 0, 0, 255, 0])))
    data = bytes(frame.planes[0])
    assert data[:3] == bytes([0, 255, 0])
    assert data[frame.planes[0].line_size:][:3] == bytes([255, 0, 0])
    assert frame.pts == 90000


@pytest.mark.asyncio
async def test_real_webrtc_multiviewer_and_identity_cleanup(monkeypatch):
    aiortc = pytest.importorskip('aiortc')
    rt = runtime()
    rt.bridge.call.return_value = SimpleNamespace(structuredContent={'supported': True, 'capacity': CAPACITY, 'name': 'fake'})
    sources = []

    class Source:
        def __init__(self, name):
            self.sequence = 0
            self.closed = False
            sources.append(self)

        def read(self, previous=0):
            self.sequence += 1
            return self.sequence, 64, 48, time.time(), bytes([40, 160, 80]) * (64 * 48)

        def close(self):
            self.closed = True

    monkeypatch.setattr('rimbot.video_stream.RawFrames', Source)
    hub = VideoHub(rt)
    client = aiortc.RTCPeerConnection(aiortc.RTCConfiguration(iceServers=[]))
    second = aiortc.RTCPeerConnection(aiortc.RTCConfiguration(iceServers=[]))
    received = asyncio.get_running_loop().create_future()
    received_second = asyncio.get_running_loop().create_future()

    @client.on('track')
    def track(track):
        received.set_result(track)

    @second.on('track')
    def second_track(track):
        received_second.set_result(track)

    try:
        client.addTransceiver('video', direction='recvonly')
        await client.setLocalDescription(await client.createOffer())
        answer = await hub.offer(Offer(connection_id='test-connection', session_id='session', viewer='a', sdp=client.localDescription.sdp))
        await client.setRemoteDescription(aiortc.RTCSessionDescription(**answer))
        track = await asyncio.wait_for(received, 5)
        frames = [await asyncio.wait_for(track.recv(), 8) for _ in range(3)]
        assert all((f.width, f.height) == (64, 48) for f in frames)
        assert frames[2].pts > frames[0].pts
        second.addTransceiver('video', direction='recvonly')
        await second.setLocalDescription(await second.createOffer())
        answer = await hub.offer(Offer(connection_id='second', session_id='session', viewer='b', sdp=second.localDescription.sdp))
        await second.setRemoteDescription(aiortc.RTCSessionDescription(**answer))
        other_track = await asyncio.wait_for(received_second, 5)
        await asyncio.wait_for(other_track.recv(), 8)
        assert len(hub.peers) == 2 and len(sources) == 1
        await hub.disconnect(PeerRequest(connection_id='test-connection', session_id='session', viewer='a'))
        assert list(hub.peers) == ['b']
        assert (await asyncio.wait_for(other_track.recv(), 5)).width == 64
        assert hub.status()['viewers'][0]['framesToEncoder'] > 0
        rt.context_token = 'new-load'
        await asyncio.wait_for(hub.task, 3)
        assert not hub.peers and hub.source is None and sources[0].closed
    finally:
        await client.close()
        await second.close()
        await hub.close()


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

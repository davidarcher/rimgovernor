import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from rimbot.bridge_server import create_app
from rimbot.video_stream import CAPACITY, Offer, VideoHub, video_frame


def runtime():
    return SimpleNamespace(connected=True, headless=False, context_token='session',
                           video_viewers={}, bridge=SimpleNamespace(call=AsyncMock()))


@pytest.mark.asyncio
async def test_signaling_origin_and_session_guards():
    pytest.importorskip('aiortc')
    rt = runtime()
    app = create_app(rt)
    app.state.rt = rt
    body = {'viewer': 'a', 'session_id': 'stale', 'sdp': 'm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\n'}
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
async def test_real_webrtc_frames_and_identity_cleanup(monkeypatch):
    aiortc = pytest.importorskip('aiortc')
    rt = runtime()
    rt.bridge.call.return_value = SimpleNamespace(structuredContent={'supported': True, 'capacity': CAPACITY, 'name': 'fake'})
    sources = []

    class Source:
        def __init__(self, name):
            self.sequence = 0
            self.closed = False
            sources.append(self)

        def read(self):
            self.sequence += 1
            return self.sequence, 64, 48, time.time(), bytes([40, 160, 80]) * (64 * 48)

        def close(self):
            self.closed = True

    monkeypatch.setattr('rimbot.video_stream.RawFrames', Source)
    hub = VideoHub(rt)
    client = aiortc.RTCPeerConnection(aiortc.RTCConfiguration(iceServers=[]))
    received = asyncio.get_running_loop().create_future()

    @client.on('track')
    def track(track):
        received.set_result(track)

    try:
        client.addTransceiver('video', direction='recvonly')
        await client.setLocalDescription(await client.createOffer())
        answer = await hub.offer(Offer(session_id='session', viewer='a', sdp=client.localDescription.sdp))
        await client.setRemoteDescription(aiortc.RTCSessionDescription(**answer))
        track = await asyncio.wait_for(received, 5)
        frames = [await asyncio.wait_for(track.recv(), 8) for _ in range(3)]
        assert all((f.width, f.height) == (64, 48) for f in frames)
        assert frames[2].pts > frames[0].pts
        rt.context_token = 'new-load'
        await asyncio.wait_for(hub.task, 3)
        assert not hub.peers and hub.source is None and sources[0].closed
    finally:
        await client.close()
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

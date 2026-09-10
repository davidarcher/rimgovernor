"""Demand-driven native capture with frame-bound WebSocket delivery."""
import asyncio
import ctypes
from collections import deque
from fractions import Fraction
import mmap
import math
import json
import os
import re
import struct
import sys
import time

from fastapi import APIRouter, Request, WebSocket, WebSocketDisconnect
from pydantic import BaseModel, ConfigDict, Field

router = APIRouter(prefix='/api/video')
CAPACITY = 40 + 3840 * 2160 * 4


class PeerRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)
    viewer: str = Field(min_length=1, max_length=80)
    connection_id: str = Field(min_length=1, max_length=80)


class SocketRequest(PeerRequest):
    hardware: bool = False


class RawFrames:
    def __init__(self, name, pixel_bytes=3, top_down=False, bgra=False):
        self.fd = None
        self.pixel_bytes = pixel_bytes
        self.top_down, self.bgra = top_down, bgra
        self.readback_ms = None
        if sys.platform == 'linux' and re.fullmatch(r'/dev/shm/RimGovernorVideo-[a-f0-9]{32}', name):
            self.fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW)
            try:
                if os.fstat(self.fd).st_size != CAPACITY:
                    raise ValueError('Invalid native video buffer size')
                self.buffer = mmap.mmap(self.fd, CAPACITY, access=mmap.ACCESS_READ)
            except BaseException:
                os.close(self.fd)
                raise
            return
        if sys.platform != 'win32' or not re.fullmatch(r'Local\\RimGovernorVideo-[a-f0-9]{32}', name):
            raise ValueError('Unsupported native video buffer')
        from ctypes import wintypes
        self.api = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
        self.api.OpenMutexW.restype = wintypes.HANDLE
        for method in ('ReleaseMutex', 'CloseHandle'):
            getattr(self.api, method).argtypes = [wintypes.HANDLE]
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.WaitForSingleObject.restype = wintypes.DWORD
        self.gate = self.api.OpenMutexW(0x100001, False, name + '-lock')
        if not self.gate:
            raise ValueError('Native video lease expired')
        try:
            self.buffer = mmap.mmap(-1, CAPACITY, tagname=name, access=mmap.ACCESS_READ)
        except BaseException:
            self.api.CloseHandle(self.gate)
            raise

    def read(self, previous=0):
        if self.fd is not None:
            import fcntl
            try:
                fcntl.flock(self.fd, fcntl.LOCK_SH | fcntl.LOCK_NB)
            except BlockingIOError:
                return None
        elif self.api.WaitForSingleObject(self.gate, 0) not in (0, 0x80):
            return None
        try:
            sequence, width, height, captured = struct.unpack('<Qiid', self.buffer[:24])
            if not sequence or sequence == previous or not (0 < width <= 3840 and 0 < height <= 2160):
                return None
            if not 0 <= time.time() - captured < 2:
                return None
            duration = struct.unpack('<d', self.buffer[24:32])[0]
            self.readback_ms = duration if math.isfinite(duration) and duration > 0 else None
            selection, = struct.unpack_from('<i', self.buffer, 32)
            return (sequence, width, height, captured, self.buffer[40:40 + width * height * self.pixel_bytes],
                    self.pixel_bytes, self.top_down, self.bgra, selection)
        finally:
            if self.fd is not None:
                fcntl.flock(self.fd, fcntl.LOCK_UN)
            else:
                self.api.ReleaseMutex(self.gate)

    def close(self):
        self.buffer.close()
        if self.fd is not None:
            os.close(self.fd)
        else:
            self.api.CloseHandle(self.gate)


def video_frame(raw):
    from av import VideoFrame
    sequence, width, height, captured, pixels = raw[:5]
    pixel_bytes = raw[5] if len(raw) > 5 else 3
    frame = VideoFrame(width, height, 'bgra' if len(raw) > 7 and raw[7] else 'rgba' if pixel_bytes == 4 else 'rgb24')
    stride = frame.planes[0].line_size
    row = width * pixel_bytes
    padding = bytes(stride - row)
    rows = range(height) if len(raw) > 6 and raw[6] else range(height - 1, -1, -1)
    frame.planes[0].update(b''.join(pixels[y * row:(y + 1) * row] + padding for y in rows))
    frame.pts = round(captured * 90000)
    frame.time_base = Fraction(1, 90000)
    return frame


class VideoHub:
    def __init__(self, rt):
        self.rt = rt
        self.peers = {}
        self.lock = asyncio.Lock()
        self.source = None
        self.latest = None
        self.task = None
        self.session = None
        self.peer_ids = {}
        self.heartbeat_revisions = {}
        self.last_error = ''
        self.sampled_frames = 0
        self.skipped_frames = 0
        self.source_name = None
        self.native_info = {}
        if not hasattr(rt, 'streaming_viewers'):
            rt.streaming_viewers = set()

    def valid(self, session):
        return (self.rt.connected and not self.rt.headless
                and not getattr(self.rt, 'session_closing', False)
                and session == self.rt.context_token)

    def accept_heartbeat(self, viewer, revision):
        if revision is None:
            return viewer not in self.heartbeat_revisions
        if type(revision) is not int or not 0 <= revision <= 9007199254740991:
            raise ValueError('Video heartbeat revision must be a nonnegative integer')
        now = time.monotonic()
        self.heartbeat_revisions = {key: value for key, value in self.heartbeat_revisions.items() if value[1] > now}
        previous = self.heartbeat_revisions.get(viewer)
        if previous and revision <= previous[0]:
            return False
        if not previous and len(self.heartbeat_revisions) >= 128:
            raise ValueError('Too many recent video viewers')
        self.heartbeat_revisions[viewer] = (revision, now + 60)
        return True

    async def drop(self, viewer, expected=None):
        if expected is not None and self.peers.get(viewer, (None,))[0] is not expected:
            return
        entry = self.peers.pop(viewer, None)
        if entry:
            self.peer_ids.pop(viewer, None)
            self.rt.streaming_viewers.discard(viewer)
            pc, track = entry
            track.stop()
            await pc.close()

    async def open_source(self, session):
        if self.source is None:
            result = await asyncio.wait_for(self.rt.bridge.call('home/video_stream', seconds=8), 5)
            info = result.structuredContent or {}
            self.native_info = info
            if not info.get('supported') or info.get('capacity') != CAPACITY:
                raise ValueError('Native continuous video is unavailable')
            self.source = (RawFrames(info['name'], pixel_bytes=4, top_down=True, bgra=True) if info.get('format') == 'bgra32-top-down'
                           else RawFrames(info['name'], pixel_bytes=4) if info.get('format') == 'rgba32-bottom-up'
                           else RawFrames(info['name']))
            self.source_name = info['name']
            self.session = session
            self.last_error = ''
            self.sampled_frames = self.skipped_frames = 0
            self.task = asyncio.create_task(self.run(self.source, session, info['name']))

    async def disconnect(self, body):
        async with self.lock:
            if self.peer_ids.get(body.viewer) == (body.session_id, body.connection_id):
                await self.drop(body.viewer)

    def status(self):
        return {'active': self.source is not None, 'sampledFrames': self.sampled_frames,
                'skippedCaptureFrames': self.skipped_frames, 'error': self.last_error,
                'frameAgeMs': max(0, (time.time() - self.latest[3]) * 1000) if self.latest else None,
                'nativeReadbackMs': getattr(self.source, 'readback_ms', None),
                'native': {key: value for key, value in self.native_info.items() if key not in ('name', 'operation')},
                'viewers': [{'state': pc.connectionState, 'framesToEncoder': track.delivered,
                             'encoding': getattr(track, 'encoding', 'unavailable'),
                             'skippedBeforeEncoder': track.skipped,
                             'captureToDisplayMedianMs': percentile(getattr(track, 'display_ages', []), .5),
                             'captureToDisplayP95Ms': percentile(getattr(track, 'display_ages', []), .95),
                             'selectionToDisplayMedianMs': percentile(getattr(track, 'input_ages', []), .5),
                             'selectionToDisplayP95Ms': percentile(getattr(track, 'input_ages', []), .95),
                             'selectionSamples': len(getattr(track, 'input_ages', [])),
                             'captureToEncoderMedianMs': sorted(track.ages)[len(track.ages) // 2] if track.ages else None,
                             'captureToEncoderP95Ms': sorted(track.ages)[min(len(track.ages) - 1, int(len(track.ages) * .95))] if track.ages else None}
                            for pc, track in self.peers.values()]}

    async def run(self, source, session, name):
        previous = 0
        async def renew():
            while True:
                await asyncio.sleep(3)
                result = await asyncio.wait_for(self.rt.bridge.call('home/video_stream', seconds=8), 4)
                self.native_info = result.structuredContent or {}
                if (result.structuredContent or {}).get('error'):
                    raise ValueError('Native capture stopped')
                if (result.structuredContent or {}).get('name') != name:
                    raise ValueError('Native capture lease changed')

        renewal = asyncio.create_task(renew())
        try:
            while True:
                async with self.lock:
                    for viewer in list(self.peers):
                        if self.rt.video_viewers.get(viewer, 0) <= time.monotonic():
                            await self.drop(viewer)
                    if self.source is not source:
                        return
                    if not self.valid(session) or not self.peers or renewal.done():
                        if renewal.done() and not renewal.cancelled():
                            error = renewal.exception()
                            if error:
                                self.last_error = str(error)[:200]
                        await self.release()
                        return
                    # One latest frame, independent of slow native lease renewal or reviews.
                    raw = source.read(previous)
                    if raw:
                        if previous:
                            self.skipped_frames += max(0, raw[0] - previous - 1)
                        previous = raw[0]
                        self.sampled_frames += 1
                        self.latest = raw
                await asyncio.sleep(1 / 120)
        except Exception as error:
            self.last_error = str(error)[:200]
        finally:
            renewal.cancel()
            await asyncio.gather(renewal, return_exceptions=True)
            async with self.lock:
                if self.source is source:
                    await self.release()

    async def release(self):
        for viewer in list(self.peers):
            await self.drop(viewer)
        if self.source:
            self.source.close()
            self.source = None
        self.latest = None
        # Native lease expires independently even if GABS is blocked or disconnected.

    async def close(self):
        if self.task:
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)
        for viewer in list(self.peers):
            await self.drop(viewer)


@router.post('/close')
async def close_peer(body: PeerRequest, request: Request):
    await request.app.state.video.disconnect(body)
    return {'closed': True}


@router.get('/status')
async def video_status(request: Request):
    return request.app.state.video.status()


def percentile(values, fraction):
    return sorted(values)[min(len(values) - 1, int(len(values) * fraction))] if values else None


class JpegEncoder:
    def __init__(self):
        self.codec = None
        self.size = None

    def encode(self, raw):
        import av
        size = raw[1:3]
        if size != self.size:
            self.codec = av.CodecContext.create('mjpeg', 'w')
            self.codec.width, self.codec.height = size
            self.codec.pix_fmt = 'yuvj420p'
            self.codec.time_base = Fraction(1, 90000)
            self.codec.thread_count = 1
            self.size = size
        frame = video_frame(raw).reformat(format='yuvj420p')
        return b''.join(bytes(packet) for packet in self.codec.encode(frame))


class HardwareEncoder:
    """Independent H.264 frames: no delayed output or inter-frame replay dependency."""
    def __init__(self):
        self.codec = None
        self.size = None

    def encode(self, raw):
        import av
        size = raw[1:3]
        if size != self.size:
            self.codec = av.CodecContext.create('h264_nvenc', 'w')
            self.codec.width, self.codec.height = size
            self.codec.pix_fmt = 'yuv420p'
            self.codec.time_base = Fraction(1, 90000)
            self.codec.gop_size = 1
            self.codec.max_b_frames = 0
            self.codec.options = {'preset': 'p1', 'tune': 'ull', 'zerolatency': '1',
                                  'profile': 'baseline', 'delay': '0', 'rc': 'constqp', 'qp': '22'}
            self.size = size
        packets = self.codec.encode(video_frame(raw).reformat(format='yuv420p'))
        if not packets or not all(packet.is_keyframe for packet in packets):
            raise RuntimeError('Hardware encoder did not produce an immediate independent frame')
        return b''.join(bytes(packet) for packet in packets)


class SocketFrames:
    def __init__(self, socket):
        self.socket = socket
        self.connectionState = 'connected'
        self.delivered = self.skipped = 0
        self.ages = deque(maxlen=128)
        self.display_ages = deque(maxlen=128)
        self.input_ages = deque(maxlen=128)
        self.encoding = 'jpeg'

    def stop(self):
        pass

    async def close(self):
        self.connectionState = 'closed'
        try:
            await self.socket.close()
        except (RuntimeError, WebSocketDisconnect):
            pass


@router.websocket('/frames')
async def socket_frames(socket: WebSocket):
    from urllib.parse import urlparse
    if (urlparse(socket.headers.get('origin', '')).netloc != socket.headers.get('host')
            or socket.headers.get('sec-websocket-protocol') != 'rimgovernor-view-v1'):
        await socket.close(code=1008)
        return
    try:
        body = SocketRequest.model_validate(dict(socket.query_params))
    except ValueError:
        await socket.close(code=1008)
        return
    hub = socket.app.state.video
    peer = SocketFrames(socket)
    accepted = False
    try:
        async with hub.lock:
            if not hub.valid(body.session_id) or (body.viewer not in hub.peers and len(hub.peers) >= 4):
                raise ValueError('View unavailable')
            if hub.source and hub.session != body.session_id:
                hub.task.cancel()
                await hub.release()
            await hub.drop(body.viewer)
            await hub.open_source(body.session_id)
            await socket.accept(subprotocol='rimgovernor-view-v1')
            accepted = True
            hub.peers[body.viewer] = (peer, peer)
            hub.peer_ids[body.viewer] = (body.session_id, body.connection_id)
            hub.rt.video_viewers[body.viewer] = time.monotonic() + 8
            hub.rt.streaming_viewers.add(body.viewer)
        encoder = HardwareEncoder() if body.hardware else JpegEncoder()
        peer.encoding = 'h264' if body.hardware else 'jpeg'
        previous = 0
        deadline = time.monotonic() + 3
        while hub.valid(body.session_id) and peer.connectionState == 'connected':
            raw = hub.latest
            if not raw or raw[0] == previous or time.time() - raw[3] >= .75:
                if time.monotonic() > deadline:
                    raise TimeoutError('Native stream stalled')
                await asyncio.sleep(.005)
                continue
            try:
                image = await asyncio.to_thread(encoder.encode, raw)
            except Exception:
                if peer.encoding != 'h264':
                    raise
                encoder = JpegEncoder()
                peer.encoding = 'jpeg'
                image = await asyncio.to_thread(encoder.encode, raw)
            if not hub.valid(body.session_id):
                break
            if previous:
                peer.skipped += max(0, raw[0] - previous - 1)
            previous = raw[0]
            peer.delivered += 1
            peer.ages.append(max(0, (time.time() - raw[3]) * 1000))
            metadata = json.dumps({'source': hub.source_name, 'frame': raw[0], 'width': raw[1],
                'height': raw[2], 'captured': raw[3], 'session': body.session_id,
                'encoding': peer.encoding, 'selection': raw[8] if len(raw) > 8 else None}, separators=(',', ':')).encode()
            # Exactly one unacknowledged frame. Slow viewers cannot grow a video queue.
            await asyncio.wait_for(socket.send_bytes(struct.pack('<I', len(metadata)) + metadata + image), 2)
            message = await asyncio.wait_for(socket.receive_text(), 3)
            if len(message) > 1024:
                raise ValueError('Oversized frame acknowledgement')
            ack = json.loads(message)
            if ack.get('frame') != raw[0]:
                raise ValueError('Out-of-order frame acknowledgement')
            displayed = ack.get('displayed')
            effect = ack.get('selectionEffectMs')
            if type(effect) in (int, float) and math.isfinite(effect) and 0 <= effect <= 2000:
                peer.input_ages.append(effect)
            if type(displayed) in (int, float) and math.isfinite(displayed) and abs(time.time() - displayed) < 2:
                peer.display_ages.append(max(0, (displayed - raw[3]) * 1000))
            hub.rt.video_viewers[body.viewer] = time.monotonic() + 8
            deadline = time.monotonic() + 3
    except (ValueError, OSError, RuntimeError, TimeoutError, WebSocketDisconnect) as error:
        if not isinstance(error, WebSocketDisconnect):
            hub.last_error = str(error)[:200]
    finally:
        owned = hub.peers.get(body.viewer, (None,))[0] is peer
        lease = getattr(hub.rt, 'player_input', None)
        await hub.drop(body.viewer, peer)
        if not accepted:
            await peer.close()
        # A disconnected controlling view cancels held gestures into Manual.
        if (owned and lease and getattr(hub.rt, 'player_input', None) is lease
                and lease.viewer == body.viewer and lease.session == body.session_id):
            try:
                await hub.rt.set_mode('manual', player_owner=(lease.session, lease.viewer, lease.token))
            except (ValueError, RuntimeError):
                pass  # Native held-input expiry is independent of the controller.

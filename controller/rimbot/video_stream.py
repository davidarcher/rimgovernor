"""Demand-driven native RGB capture and receive-only dashboard WebRTC."""
import asyncio
import ctypes
from fractions import Fraction
import mmap
import re
import struct
import sys
import time

from fastapi import APIRouter, Request
from pydantic import BaseModel, ConfigDict, Field

router = APIRouter(prefix='/api/video')
CAPACITY = 32 + 3840 * 2160 * 3


class Offer(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)
    viewer: str = Field(min_length=1, max_length=80)
    sdp: str = Field(min_length=1, max_length=64000)


class RawFrames:
    def __init__(self, name):
        if sys.platform != 'win32' or not re.fullmatch(r'Local\\RimBotVideo-[a-f0-9]{32}', name):
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

    def read(self):
        if self.api.WaitForSingleObject(self.gate, 0) not in (0, 0x80):
            return None
        try:
            sequence, width, height, captured = struct.unpack('<Qiid', self.buffer[:24])
            if not sequence or not (0 < width <= 3840 and 0 < height <= 2160):
                return None
            if not 0 <= time.time() - captured < 2:
                return None
            return sequence, width, height, captured, self.buffer[32:32 + width * height * 3]
        finally:
            self.api.ReleaseMutex(self.gate)

    def close(self):
        self.buffer.close()
        self.api.CloseHandle(self.gate)


def video_frame(raw):
    from av import VideoFrame
    sequence, width, height, captured, pixels = raw
    frame = VideoFrame(width, height, 'rgb24')
    stride = frame.planes[0].line_size
    row = width * 3
    padding = bytes(stride - row)
    frame.planes[0].update(b''.join(pixels[y * row:(y + 1) * row] + padding
                                   for y in range(height - 1, -1, -1)))
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
        if not hasattr(rt, 'streaming_viewers'):
            rt.streaming_viewers = set()

    def valid(self, session):
        return (self.rt.connected and not self.rt.headless
                and not getattr(self.rt, 'session_closing', False)
                and session == self.rt.context_token)

    async def offer(self, body):
        from aiortc import RTCConfiguration, RTCPeerConnection, RTCSessionDescription, VideoStreamTrack
        from aiortc.mediastreams import MediaStreamError
        # No data channel or incoming media: all game input stays on the shared writer.
        media = [line for line in body.sdp.splitlines() if line.startswith('m=')]
        if len(media) != 1 or not media[0].startswith('m=video '):
            raise ValueError('Offer must contain only one receive-only video track')
        if 'a=recvonly' not in body.sdp or 'a=sendrecv' in body.sdp or 'a=sendonly' in body.sdp:
            raise ValueError('Dashboard video must be receive-only')
        async with self.lock:
            if not self.valid(body.session_id):
                raise ValueError('Video session changed or rendering is unavailable')
            if self.source and self.session != body.session_id:
                self.task.cancel()
                await self.release()
            if body.viewer not in self.peers and len(self.peers) >= 4:
                raise ValueError('Four video viewers are already connected')
            await self.drop(body.viewer)
            if self.source is None:
                result = await asyncio.wait_for(self.rt.bridge.call('home/video_stream', seconds=8), 5)
                info = result.structuredContent or {}
                if not info.get('supported') or info.get('capacity') != CAPACITY:
                    raise ValueError('Native continuous video is unavailable')
                self.source = RawFrames(info['name'])
                self.session = body.session_id
                self.task = asyncio.create_task(self.run())
            pc = RTCPeerConnection(RTCConfiguration(iceServers=[]))
            hub = self

            class Track(VideoStreamTrack):
                def __init__(self):
                    super().__init__()
                    self.sequence = 0

                async def recv(self):
                    deadline = time.monotonic() + 3
                    while self.readyState == 'live' and hub.valid(body.session_id):
                        raw = hub.latest
                        if raw and raw[0] != self.sequence and time.time() - raw[3] < 2:
                            self.sequence = raw[0]
                            return await asyncio.to_thread(video_frame, raw)
                        if time.monotonic() > deadline:
                            break
                        await asyncio.sleep(1 / 60)
                    if hub.peers.get(body.viewer, (None,))[0] is pc:
                        asyncio.create_task(hub.drop(body.viewer))
                    raise MediaStreamError

            track = Track()
            self.peers[body.viewer] = (pc, track)
            # The lease is independently renewed by the existing dashboard heartbeat.
            self.rt.video_viewers[body.viewer] = time.monotonic() + 8

            @pc.on('connectionstatechange')
            async def changed():
                if pc.connectionState == 'connected' and self.peers.get(body.viewer, (None,))[0] is pc:
                    self.rt.streaming_viewers.add(body.viewer)
                if pc.connectionState in ('failed', 'closed'):
                    if self.peers.get(body.viewer, (None,))[0] is pc:
                        await self.drop(body.viewer)

            try:
                pc.addTrack(track)
                await pc.setRemoteDescription(RTCSessionDescription(body.sdp, 'offer'))
                await asyncio.wait_for(pc.setLocalDescription(await pc.createAnswer()), 5)
                if not self.valid(body.session_id):
                    raise ValueError('Video session changed during negotiation')
                return {'sdp': pc.localDescription.sdp, 'type': pc.localDescription.type}
            except BaseException:
                await self.drop(body.viewer)
                raise

    async def drop(self, viewer):
        entry = self.peers.pop(viewer, None)
        if entry:
            self.rt.streaming_viewers.discard(viewer)
            pc, track = entry
            track.stop()
            await pc.close()

    async def run(self):
        source = self.source
        async def renew():
            while True:
                await asyncio.sleep(3)
                result = await asyncio.wait_for(self.rt.bridge.call('home/video_stream', seconds=8), 4)
                if (result.structuredContent or {}).get('error'):
                    raise ValueError('Native capture stopped')

        renewal = asyncio.create_task(renew())
        try:
            while True:
                async with self.lock:
                    for viewer in list(self.peers):
                        if self.rt.video_viewers.get(viewer, 0) <= time.monotonic():
                            await self.drop(viewer)
                    if not self.source or not self.valid(self.session) or not self.peers or renewal.done():
                        await self.release()
                        return
                    # One latest frame, independent of slow native lease renewal or reviews.
                    raw = self.source.read()
                    if raw:
                        self.latest = raw
                await asyncio.sleep(1 / 30)
        except Exception:
            pass  # Browser detects a stalled stream and displays the snapshot fallback.
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


@router.post('/offer')
async def offer(body: Offer, request: Request):
    from fastapi.responses import JSONResponse
    hub = request.app.state.video
    try:
        return await hub.offer(body)
    except (ImportError, TimeoutError, RuntimeError, KeyError, OSError):
        return JSONResponse({'detail': 'Continuous video unavailable; using snapshots'}, status_code=503)

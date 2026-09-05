"""RIMAPI's JPEG-over-UDP camera, relayed as binary WebSocket frames.

Upstream packets have no frame ID. Require ordered chunks and discard incomplete
frames, rather than combining packets from different captures. No frame history.
"""
import asyncio
import struct
import time


class JPEGReceiver(asyncio.DatagramProtocol):
    def __init__(self, frame):
        self.frame = frame
        self.parts = []
        self.total = 0
        self.started = 0

    def datagram_received(self, data, addr):
        if addr[0] != '127.0.0.1' or len(data)<9 or data[:3]!=b'CAM':
            return
        length = struct.unpack('<I',data[3:7])[0]
        index, total = data[7], data[8]
        if length != len(data)-9 or not 1<=total<=128 or index>=total:
            self.parts=[]
            return
        if index == 0:
            self.parts=[]
            self.total=total
            self.started=time.monotonic()
        if index != len(self.parts) or total!=self.total or time.monotonic()-self.started>1:
            self.parts=[]
            return
        self.parts.append(data[9:])
        if len(self.parts)==total:
            jpeg=b''.join(self.parts)
            self.parts=[]
            if jpeg.startswith(b'\xff\xd8') and jpeg.endswith(b'\xff\xd9'):
                self.frame(jpeg)


class Video:
    def __init__(self, rt):
        self.rt=rt
        self.transport=None
        self.queues=set()
        self.lock=asyncio.Lock()
        self.owner_api=None
        self.previous=None

    def frame(self, data):
        for q in self.queues:
            if q.full():
                q.get_nowait()
            q.put_nowait(data)

    async def subscribe(self):
        async with self.lock:
            if not self.transport:
                api=self.rt.api
                previous=await api.request('GET','/api/v1/camera/stream/status')
                if previous.get('is_streaming'):
                    raise ValueError('RIMAPI camera is already streaming to another client. Stop that stream before connecting here.')
                transport,_=await asyncio.get_running_loop().create_datagram_endpoint(lambda:JPEGReceiver(self.frame),local_addr=('127.0.0.1',0))
                port=transport.get_extra_info('sockname')[1]
                try:
                    await api.request('POST','/api/v1/camera/stream/setup',body={'port':port,'address':'127.0.0.1','frame_width':1280,'frame_height':720,'target_fps':15,'jpeg_quality':65})
                    await api.request('POST','/api/v1/camera/stream/start')
                except BaseException:
                    transport.close()
                    try:
                        await api.request('POST','/api/v1/camera/stream/stop')
                    except Exception:
                        pass
                    raise
                self.transport,self.owner_api,self.previous=transport,api,previous.get('config')
            queue=asyncio.Queue(maxsize=1)
            self.queues.add(queue)
            return queue

    async def unsubscribe(self,queue):
        async with self.lock:
            self.queues.discard(queue)
            if not self.queues and self.transport:
                await self._close()

    async def _close(self):
        self.transport.close()
        self.transport=None
        try:
            await self.owner_api.request('POST','/api/v1/camera/stream/stop')
            if self.previous:
                await self.owner_api.request('POST','/api/v1/camera/stream/setup',body=self.previous)
        except Exception:
            pass
        self.owner_api=None

    async def close(self):
        async with self.lock:
            if self.transport:
                await self._close()
            self.queues.clear()

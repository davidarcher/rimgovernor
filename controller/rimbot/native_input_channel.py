"""Single outstanding player message on the native main-thread mailbox."""
import asyncio
import mmap
import os
import re
import struct
import time


class NativeInputChannel:
    def __init__(self, name, capacity):
        if capacity != 4096 or not re.fullmatch(r'/dev/shm/RimBotInput-[a-f0-9]{32}', name):
            raise ValueError('Invalid private input channel')
        self.fd = os.open(name, os.O_RDWR | os.O_NOFOLLOW)
        try:
            if os.fstat(self.fd).st_size != capacity:
                raise ValueError('Invalid input buffer size')
            self.buffer = mmap.mmap(self.fd, capacity)
        except BaseException:
            os.close(self.fd)
            raise
        self.lock = asyncio.Lock()
        self.closed = False

    async def call(self, *, action, owner, source='', frame=0, order=0,
                   kind='', x=0, y=0, button=0, key='', delta=0):
        import fcntl
        data = struct.pack('<qqiiii', frame, order, x, y, button, delta)
        for value in (action, owner, source, kind, key):
            encoded = value.encode('utf8')
            if len(encoded) > 300:
                raise ValueError('Input text too long')
            data += struct.pack('<i', len(encoded)) + encoded
        async with self.lock:
            if self.closed:
                raise ValueError('Native input channel closed')
            deadline = time.monotonic() + .5
            command = None
            while time.monotonic() < deadline:
                try:
                    fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    await asyncio.sleep(.002)
                    continue
                try:
                    sent, received = struct.unpack_from('<qq', self.buffer)
                    if command is None:
                        if sent != received:
                            raise ValueError('Previous native input is uncertain; release and inspect')
                        command = sent + 1
                        struct.pack_into('<i', self.buffer, 32, len(data))
                        self.buffer[36:36 + len(data)] = data
                        struct.pack_into('<q', self.buffer, 0, command)
                    elif received == command:
                        status, length, buttons, keys = struct.unpack_from('<iiii', self.buffer, 16)
                        if status != 1:
                            detail = bytes(self.buffer[2084:2084 + min(max(length, 0), 1200)]).decode('utf8', errors='replace')
                            raise ValueError(detail or 'Native input refused')
                        return {'success': True, 'released': action == 'release',
                                'order': order, 'heldButtons': buttons, 'heldKeys': keys}
                finally:
                    fcntl.flock(self.fd, fcntl.LOCK_UN)
                await asyncio.sleep(.002)
            raise TimeoutError('Native input receipt timed out; event was not replayed')

    def close(self):
        if not self.closed:
            self.closed = True
            self.buffer.close()
            os.close(self.fd)

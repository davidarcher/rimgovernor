import asyncio
import mmap
import os
from pathlib import Path
import struct
import sys
import uuid

import pytest

from rimgovernor.native_input_channel import NativeInputChannel


pytestmark = pytest.mark.skipif(sys.platform != 'linux', reason='Private Linux native input mailbox')


@pytest.mark.asyncio
async def test_real_mailbox_order_refusal_and_no_replay():
    import fcntl
    path = Path('/dev/shm') / ('RimGovernorInput-' + uuid.uuid4().hex)
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
    os.ftruncate(fd, 4096)
    native = mmap.mmap(fd, 4096)
    channel = NativeInputChannel(str(path), 4096)
    seen = []

    async def game():
        while len(seen) < 2:
            fcntl.flock(fd, fcntl.LOCK_EX)
            try:
                sent, received = struct.unpack_from('<qq', native)
                if sent != received:
                    frame, order, x, y, button, delta = struct.unpack_from('<qqiiii', native, 36)
                    seen.append((sent, frame, order, x, y))
                    message = b'view changed' if sent == 2 else b''
                    struct.pack_into('<iiii', native, 16, -1 if message else 1, len(message), 1, 0)
                    native[2084:2084 + len(message)] = message
                    struct.pack_into('<q', native, 8, sent)
            finally:
                fcntl.flock(fd, fcntl.LOCK_UN)
            await asyncio.sleep(.001)

    task = asyncio.create_task(game())
    try:
        result = await channel.call(action='event', owner='owner', frame=3, order=1, kind='down', x=12, y=34)
        assert result['heldButtons'] == 1
        with pytest.raises(ValueError, match='view changed'):
            await channel.call(action='event', owner='owner', frame=4, order=2, kind='up', x=15, y=35)
        await task
        assert seen == [(1, 3, 1, 12, 34), (2, 4, 2, 15, 35)]
        with pytest.raises(TimeoutError, match='not replayed'):
            await channel.call(action='event', owner='owner', frame=5, order=3, kind='down')
        assert struct.unpack_from('<q', native)[0] == 3
        with pytest.raises(ValueError, match='uncertain'):
            await channel.call(action='event', owner='owner', frame=6, order=4, kind='down')
        assert struct.unpack_from('<q', native)[0] == 3
    finally:
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
        channel.close(); native.close(); os.close(fd); path.unlink()


def test_channel_rejects_arbitrary_file_and_capacity():
    with pytest.raises(ValueError):
        NativeInputChannel('/etc/passwd', 4096)
    with pytest.raises(ValueError):
        NativeInputChannel('/dev/shm/RimGovernorInput-' + 'a' * 32, 8192)

"""Receive native framebuffer video over WebRTC in a disposable paused colony."""
import argparse
import asyncio
import hashlib
import json
import time
from pathlib import Path
from types import SimpleNamespace

from aiortc import RTCConfiguration, RTCPeerConnection, RTCSessionDescription
from rimbot.bridge import bridge_session
from rimbot.clock_control import PlayClock
from rimbot.headless import isolated_root, prepare_rendered, rendered_headless_mismatch
from rimbot.video_stream import Offer, VideoHub


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare_rendered(root)
    report = {'outcome': 'failed', 'scope': 'Paused native framebuffer to aiortc receiver; not Chrome display latency or simulation throughput'}
    client = RTCPeerConnection(RTCConfiguration(iceServers=[]))
    received = asyncio.get_running_loop().create_future()
    hub = None

    @client.on('track')
    def track(track):
        received.set_result(track)

    try:
        async with bridge_session(root / 'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', config) as bridge:
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000,
                                  ignoreModCompatibility=rendered_headless_mismatch(root))
                await PlayClock(bridge).change('Paused')
                report['before'] = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent
                rt = SimpleNamespace(bridge=bridge, connected=True, headless=False,
                                     context_token='acceptance', video_viewers={})
                hub = VideoHub(rt)
                client.addTransceiver('video', direction='recvonly')
                await client.setLocalDescription(await client.createOffer())
                answer = await hub.offer(Offer(session_id='acceptance', viewer='probe', sdp=client.localDescription.sdp))
                await client.setRemoteDescription(RTCSessionDescription(**answer))
                track = await asyncio.wait_for(received, 10)
                started = time.monotonic()
                count = 0
                hashes = set()
                while time.monotonic() - started < args.seconds:
                    rt.video_viewers['probe'] = time.monotonic() + 8
                    frame = await asyncio.wait_for(track.recv(), 12)
                    count += 1
                    hashes.add(hashlib.sha256(bytes(frame.planes[0])).hexdigest())
                    report['dimensions'] = [frame.width, frame.height]
                report.update(frames=count, seconds=time.monotonic() - started,
                              fps=count / (time.monotonic() - started), unique_decoded_frames=len(hashes))
                await hub.close()
                await bridge.call('home/video_stream', seconds=0)
                report['after'] = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent
                assert count >= 10, report
                assert report['after']['time']['paused']
                assert report['before']['time']['ticksGame'] == report['after']['time']['ticksGame']
                assert not hub.peers and hub.source is None
                report['outcome'] = 'passed'
            finally:
                if hub:
                    await hub.close()
                await client.close()
                await bridge.core('games_stop', gameId=bridge.game_id)
    except Exception as error:
        report['error'] = repr(error)
    finally:
        await client.close()
        (args.output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=float, default=10)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)

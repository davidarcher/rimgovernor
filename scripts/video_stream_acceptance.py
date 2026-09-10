"""Receive native framebuffer video over WebRTC in a disposable paused colony."""
import argparse
import asyncio
import hashlib
import json
import time
from pathlib import Path
from types import SimpleNamespace

from aiortc import RTCConfiguration, RTCPeerConnection, RTCSessionDescription
from rimbot.bridge import bridge_session, gabs_executable
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
        async with bridge_session(gabs_executable(root, config), config) as bridge:
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000,
                                  ignoreModCompatibility=rendered_headless_mismatch(root))
                await PlayClock(bridge).change('Paused')
                report['input_contracts'] = {}
                for tool in ('rimworld/click_cell', 'rimworld/drag_cell', 'rimworld/set_hover_target',
                             'rimworld/clear_hover_target', 'rimworld/scroll_ui_target',
                             'rimworld/get_camera_state', 'rimworld/get_selection_semantics'):
                    report['input_contracts'][tool] = (await bridge.detail(tool)).structuredContent
                report['before'] = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent
                if args.input_probe:
                    report['native_input'] = {'events': []}
                    async def native(tool, **arguments):
                        result = (await bridge.call(tool, **arguments)).structuredContent
                        report['native_input']['events'].append({'tool': tool, 'arguments': arguments, 'result': result})
                        return result
                    status = await native('home/status', colonists=True, threats=False)
                    pawn = status['colonists'][0]
                    position = pawn['position']
                    await native('rimworld/clear_selection')
                    await native('rimworld/click_cell', x=position['x'], z=position['z'], button='left')
                    selected = await native('rimworld/get_selection_semantics')
                    assert pawn['thingId'] in [p['id'] for p in selected['selectedObjects']], selected
                    # Selection of this uninjured pawn offers no guaranteed vanilla
                    # menu. Retain the right-click outcome without calling it menu acceptance.
                    right = await native('rimworld/click_cell', x=position['x'], z=position['z'], button='right')
                    report['native_input']['right_click_menu_opened'] = right.get('actionKind') == 'menu_opened'
                    if report['native_input']['right_click_menu_opened']:
                        await native('rimworld/get_context_menu_options')
                        await native('rimworld/close_context_menu')
                    await native('rimworld/clear_selection')
                    await native('rimworld/drag_cell', fromX=position['x']-1, fromZ=position['z']-1,
                                 toX=position['x']+1, toZ=position['z']+1, button='left', modifiers='shift')
                    selected = await native('rimworld/get_selection_semantics')
                    assert pawn['thingId'] in [p['id'] for p in selected['selectedObjects']], selected
                    await native('rimworld/clear_selection')
                    report['native_input']['passed'] = True
                rt = SimpleNamespace(bridge=bridge, connected=True, headless=False,
                                     context_token='acceptance', video_viewers={})
                hub = VideoHub(rt)
                client.addTransceiver('video', direction='recvonly')
                await client.setLocalDescription(await client.createOffer())
                answer = await hub.offer(Offer(connection_id='test-connection', session_id='acceptance', viewer='probe', sdp=client.localDescription.sdp))
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
                report['delivery'] = hub.status()
                import av
                with av.open(str(args.output / 'frame.png'), 'w', format='image2') as output:
                    stream = output.add_stream('png', rate=1)
                    stream.width, stream.height, stream.pix_fmt = frame.width, frame.height, 'rgb24'
                    for packet in stream.encode(frame.reformat(format='rgb24')):
                        output.mux(packet)
                    for packet in stream.encode():
                        output.mux(packet)
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
    parser.add_argument('--input-probe', action='store_true', help='Accept native selection and shift-drag; record right-click outcome, not browser coordinates')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)

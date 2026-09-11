"""Verify native input and decode the current WebSocket stream in a private colony."""
import argparse
import asyncio
import hashlib
import json
import time
import traceback
import io
import socket
import struct
from pathlib import Path
from types import SimpleNamespace

import av
import uvicorn
from fastapi import FastAPI
from websockets.asyncio.client import connect
from rimgovernor.bridge import bridge_session, gabs_executable
from rimgovernor.clock_control import PlayClock
from rimgovernor.headless import isolated_root, prepare_rendered
from rimgovernor.video_stream import VideoHub, router


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare_rendered(root)
    report = {'outcome': 'failed', 'scope': 'Paused native framebuffer through production WebSocket/JPEG delivery to a decoder; not browser display latency or simulation throughput'}
    hub = None
    server = server_task = listener = None

    try:
        async with bridge_session(gabs_executable(root, config), config) as bridge:
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000,
                                  ignoreModCompatibility=False)
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
                if args.extended_input:
                    report.setdefault('native_input', {'events': []})
                    roster=(await bridge.call('home/status', colonists=True, threats=False)).structuredContent
                    position=roster['colonists'][0]['position']
                    for query in ('designator', 'context_menu', 'ui_target'):
                        catalog = (await bridge.names(query=query)).structuredContent
                        assert not catalog.get('nextCursor'), catalog
                        for row in catalog.get('tools', []):
                            report['input_contracts'][row['gabpName']] = (await bridge.detail(row['gabpName'])).structuredContent
                    async def input_call(tool, **arguments):
                        report['input_contracts'][tool] = (await bridge.detail(tool)).structuredContent
                        value = (await bridge.call(tool, **arguments)).structuredContent
                        report['native_input']['events'].append(dict(tool=tool, arguments=arguments, result=value))
                        return value
                    await input_call('rimworld/open_main_tab', mainTabId='Work')
                    status = await input_call('home/status', colonists=False, threats=False)
                    assert status['ui']['mainTabOpen'] and status['ui']['mainTabDefName']=='Work', status
                    await input_call('rimworld/close_main_tab')
                    status = await input_call('home/status', colonists=False, threats=False)
                    assert not status['ui']['mainTabOpen'], status
                    stock = await input_call('home/list_things', match='WoodLog', includeHeld=False, maxPositionsPerDef=10)
                    menu = None
                    for row in stock['things']:
                        if row['defName']!='WoodLog':continue
                        for item in row['positions']:
                            cell=item.get('position',item)
                            clicked=await input_call('rimworld/click_cell', x=position['x'], z=position['z'], button='left')
                            clicked=await input_call('rimworld/click_cell', x=cell['x'], z=cell['z'], button='right')
                            if clicked.get('actionKind')=='menu_opened':
                                menu=await input_call('rimworld/get_context_menu_options')
                                await input_call('rimworld/close_context_menu')
                                break
                        if menu is not None:break
                    assert menu is not None, 'No native context menu appeared for a selected pawn and observed wood'
                    report['native_input']['context_menu']=menu
                    facts=await input_call('home/colony_facts', planning=True)
                    legal={(c['x'],c['z']) for c in facts['cells'] if c['walkable'] and not c['occupied'] and not c.get('zone')}
                    camera=await input_call('rimworld/get_camera_state')
                    center=camera['mapPosition']
                    site=next(((x,z) for x,z in sorted(legal,key=lambda p:(p[0]-center['x'])**2+(p[1]-center['z'])**2) if all((x+dx,z+dz) in legal
                               for dx in range(3) for dz in range(3))),None)
                    assert site, 'No observed legal 3x3 stockpile gesture site'
                    x,z=site
                    designators=await input_call('rimworld/list_architect_designators', categoryId='Zone', includeHidden=False)
                    stockpile=next(d for d in designators['designators'] if d.get('zoneTypeName')=='RimWorld.Zone_Stockpile')
                    await input_call('rimworld/clear_selection')
                    await input_call('rimworld/select_architect_designator', designatorId=stockpile['id'])
                    await input_call('rimworld/drag_cell', fromX=x, fromZ=z, toX=x+2, toZ=z+2, button='left')
                    cell=await input_call('rimworld/get_cell_info', x=x+1,z=z+1)
                    assert cell['cell'].get('zone') and cell['cell']['zone']['cellCount']==9, ('Native stockpile drag did not create nine cells',cell)
                    report['native_input']['placement_gesture']=dict(site=site,readback=cell,
                        scope='Native stockpile drag through live play UI; no completed pawn-work claim')
                    await input_call('rimworld/click_cell',x=x,z=z,button='right')
                    for dx,dz in ((10000,0),(-10000,0),(0,10000),(0,-10000)):
                        await input_call('rimworld/move_camera', deltaX=dx, deltaZ=dz)
                        first = await input_call('rimworld/get_camera_state')
                        await input_call('rimworld/move_camera', deltaX=dx, deltaZ=dz)
                        second = await input_call('rimworld/get_camera_state')
                        assert first['mapPosition']==second['mapPosition'], (first,second)
                    report['native_input']['extended_passed'] = True
                rt = SimpleNamespace(bridge=bridge, connected=True, headless=False,
                                     context_token='acceptance', video_viewers={})
                hub = VideoHub(rt)
                app=FastAPI();app.state.video=hub;app.include_router(router)
                listener=socket.socket();listener.bind(('127.0.0.1',0))
                port=listener.getsockname()[1]
                server=uvicorn.Server(uvicorn.Config(app,log_level='warning',lifespan='off'))
                server_task=asyncio.create_task(server.serve(sockets=[listener]))
                async with asyncio.timeout(10):
                    while not server.started:
                        if server_task.done():await server_task
                        await asyncio.sleep(.01)
                started = time.monotonic()
                count = 0
                hashes = set()
                url=f'ws://127.0.0.1:{port}/api/video/frames?session_id=acceptance&viewer=probe&connection_id=probe&hardware=false'
                async with connect(url,origin=f'http://127.0.0.1:{port}',subprotocols=['rimgovernor-view-v1'],max_size=16000000) as client:
                    while time.monotonic() - started < args.seconds:
                        packet=await asyncio.wait_for(client.recv(),12)
                        length,=struct.unpack_from('<I',packet)
                        metadata=json.loads(packet[4:4+length])
                        with av.open(io.BytesIO(packet[4+length:])) as decoded:
                            frame=next(decoded.decode(video=0))
                        count += 1
                        hashes.add(hashlib.sha256(bytes(frame.planes[0])).hexdigest())
                        report['dimensions'] = [frame.width, frame.height]
                        await client.send(json.dumps({'frame':metadata['frame']}))
                report.update(frames=count, seconds=time.monotonic() - started,
                              fps=count / (time.monotonic() - started), unique_decoded_frames=len(hashes))
                report['delivery'] = hub.status()
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
                await bridge.core('games_stop', gameId=bridge.game_id)
    except Exception as error:
        report['error'] = repr(error)
        report['traceback'] = traceback.format_exc()
    finally:
        try:
            if server:
                server.should_exit=True
            if server_task:
                await asyncio.wait_for(server_task,15)
        except Exception as error:
            report['outcome']='failed'
            report['server_cleanup_error']=repr(error)
        finally:
            if listener:
                listener.close()
            (args.output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=float, default=10)
    parser.add_argument('--input-probe', action='store_true', help='Accept native selection and shift-drag; record right-click outcome, not browser coordinates')
    parser.add_argument('--extended-input', action='store_true', help='Verify native Work tab and camera edge clamping')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)

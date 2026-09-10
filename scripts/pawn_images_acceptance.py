"""Native portraits and offscreen follow captures in a disposable rendered colony."""
import argparse
import asyncio
import base64
import json
from pathlib import Path

from rimgovernor.bridge import bridge_session, gabs_executable, runtime_file_read
from rimgovernor.clock_control import PlayClock
from rimgovernor.headless import isolated_root, prepare_rendered, rendered_headless_mismatch


async def run(args):
    args.output = args.output.resolve()
    if args.game_root:
        args.game_root = args.game_root.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output / 'bridge')
    config_dir = prepare_rendered(root)
    if args.game_root:
        path = config_dir / 'config.json'
        config = json.loads(path.read_text())
        game = config['games']['rimgovernor-trial']
        game['target'] = str(args.game_root / 'RimWorldWin64.exe')
        game['workingDir'] = str(args.game_root)
        path.write_text(json.dumps(config, indent=2))
    report = {'passed': False, 'scope': 'Native pawn images, paused camera/selection invariance and moving-pawn frames; not browser video throughput'}
    try:
        async with bridge_session(gabs_executable(root, config_dir), config_dir) as bridge:
            try:
                async def read(tool, **arguments):
                    if tool in {'home/pawn_image', 'home/list_pawns', 'home/list_things',
                                'home/colony_identity', 'rimworld/get_camera_state',
                                'rimworld/get_selection_semantics'}:
                        reply = await runtime_file_read(bridge.call, tool, **arguments)
                    else:
                        # Fixture writes are never retried after uncertain delivery.
                        reply = await bridge.call(tool, **arguments)
                    result = reply.structuredContent
                    if not isinstance(result, dict) or result.get('success') is False:
                        raise ValueError(f'{tool}: {result}')
                    return result
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await read('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                           readiness='visual', timeoutMs=90000,
                           ignoreModCompatibility=rendered_headless_mismatch(root))
                clock = PlayClock(bridge)
                await clock.change('Paused')
                identity = await read('home/colony_identity')
                session = f"{identity['colonyId']}:{identity['mapId']}:{identity['loadToken']}"
                report['schema'] = (await bridge.detail('home/pawn_image')).structuredContent
                # The followed pawn must still render when outside the main viewport.
                await read('rimworld/jump_camera_to_cell', x=20, z=20)
                report['camera_before'] = await read('rimworld/get_camera_state')
                report['pawns'] = await read('home/list_pawns', colonistsOnly=True, health=True,
                                            needs=True, equipment=True, bio=True, thoughts=True)
                await read('rimworld/select_pawn', pawnId=report['pawns']['pawns'][-1]['thingId'], append=False)
                report['selection_before'] = await read('rimworld/get_selection_semantics')
                report['images'] = []
                async def capture(pawn, view, name):
                    result = await read('home/pawn_image', pawnId=pawn['thingId'], sessionId=session, view=view)
                    encoded = result.pop('pngBase64')
                    (args.output / (name+'.png')).write_bytes(base64.b64decode(encoded, validate=True))
                    assert result['pawnId'] == pawn['thingId'] and result['sessionId'] == session
                    report['images'].append({'file': name+'.png', **result})
                    return result
                for index, pawn in enumerate(report['pawns']['pawns']):
                    await capture(pawn, 'portrait', f'portrait-{index}')
                pawn = report['pawns']['pawns'][0]
                await capture(pawn, 'follow', 'follow-before')
                report['camera_after'] = await read('rimworld/get_camera_state')
                report['selection_after'] = await read('rimworld/get_selection_semantics')
                def state(payload):
                    return {key: value for key, value in payload.items() if key != 'operation'}
                assert state(report['camera_before']) == state(report['camera_after'])
                for field in ('selectedCount', 'selectionFingerprint', 'selectedObjects'):
                    assert report['selection_before'][field] == report['selection_after'][field]
                assert (await read('home/colony_identity'))['tick'] == identity['tick']
                from rimgovernor.bridge import BridgeError
                try:
                    report['stale_refusal'] = (await bridge.call('home/pawn_image', pawnId=pawn['thingId'], sessionId='old', view='follow')).structuredContent
                    assert report['stale_refusal'].get('success') is not True
                except BridgeError as error:
                    report['stale_refusal'] = str(error)
                await clock.change('Normal', max_ticks=300)
                await asyncio.sleep(3)
                await clock.change('Paused')
                report['pawns_after'] = await read('home/list_pawns', colonistsOnly=True)
                await capture(pawn, 'follow', 'follow-after')
                moved = next(p for p in report['pawns_after']['pawns'] if p['thingId'] == pawn['thingId'])
                assert moved['position'] != pawn['position'], 'Fixture pawn did not move'
                assert state(await read('rimworld/get_camera_state')) == state(report['camera_before'])
                if args.equip_preview:
                    weapons = await read('home/list_things', category='weapons', includeHeld=False,
                                         x=moved['position']['x'], z=moved['position']['z'], radius=20)
                    report['weapons'] = weapons
                    positions = [position for row in weapons['things'] if row.get('weapon', {}).get('ranged')
                                 for position in row['positions'] if position.get('stackCount') == 1]
                    positions.sort(key=lambda p: abs(p['x']-moved['position']['x']) + abs(p['z']-moved['position']['z']))
                    assert positions, 'Fixture needs a nearby owned, unstacked ranged weapon on the ground'
                    target = positions[0]['thingId']
                    report['equip_preview'] = await read('home/order', action='equip', pawn=pawn['thingId'],
                                                        target=target, watch=False, dryRun=True)
                    report['equip_order'] = await read('home/order', action='equip', pawn=pawn['thingId'],
                                                      target=target, watch=False)
                    await clock.change('Normal', max_ticks=1200)
                    for _ in range(30):
                        await asyncio.sleep(.5)
                        await clock.poll()
                        roster = await read('home/list_pawns', colonistsOnly=True, equipment=True)
                        equipped = next(p for p in roster['pawns'] if p['thingId'] == pawn['thingId'])
                        if (equipped['equipment'].get('primary') or {}).get('thingId') == target:
                            break
                    await clock.change('Paused')
                    report['equipped'] = equipped
                    assert (equipped['equipment'].get('primary') or {}).get('thingId') == target
                    await capture(equipped, 'portrait', 'portrait-equipped')
                    await capture(equipped, 'follow', 'follow-equipped')
                report['passed'] = True
            finally:
                try:
                    report['cleanup'] = (await bridge.core('games_stop', gameId=bridge.game_id)).structuredContent
                except Exception as error:
                    report['cleanup_error'] = str(error)
                    report['passed'] = False
    except Exception as error:
        import traceback
        report['error'] = str(error)
        report['traceback'] = traceback.format_exc()
        raise
    finally:
        (args.output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    if not report['passed']:
        raise RuntimeError('Native pawn image acceptance failed; see result.json')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--game-root', type=Path, help='Private Windows game directory with this task’s fixed DLLs')
    parser.add_argument('--equip-preview', action='store_true', help='Verify native weapon icon after ordinary equip/pickup in the disposable colony')
    asyncio.run(run(parser.parse_args()))

"""Verify exact naming input, stale-ID refusal and paused native readback."""
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import prepare, isolated_root
from rimbot.store import Store


async def main(rendered=False):
    root = isolated_root('.rimbot/bridge', Path('.rimbot') / f'dialog-text-smoke-{time.time_ns()}')
    config_dir = prepare(root)
    if rendered:
        config_file = config_dir/'config.json'
        config = json.loads(config_file.read_text())
        config['games']['rimbot-trial']['args'] = [a for a in config['games']['rimbot-trial']['args']
                                                  if a not in ('-batchmode', '-nographics')]
        config_file.write_text(json.dumps(config))
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', config_dir) as bridge:
        await bridge.core('games_start', gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                          readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
        await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        store = Store(root/'dialog.sqlite')
        rt = BridgeRuntime(store, root)
        rt.bridge, rt.game = bridge, BridgeGame(bridge)
        try:
            await rt.sync_identity()
            rt.mode = 'automate'
            await bridge.call('rimworld/open_window_by_type', windowType='Dialog_NamePlayerFaction', replaceExisting=False)
            fields = await rt.game.invoke('home/dialog_text', {'list': True, 'dryRun': True})
            assert fields.get('writable') is True, fields
            assert any(f['name'] == 'curName' for f in fields['fields']), fields
            args = {'windowId': fields['windowId'], 'field': 'curName', 'text': 'Fixture Tribe', 'dryRun': False}
            for bad in (dict(args, windowId=-1), dict(args, field='cur')):
                try:
                    await rt.game.invoke('home/dialog_text', bad, allow_write=True)
                except (ValueError, BridgeError):
                    pass
                else:
                    raise AssertionError('Invalid naming target was accepted')
            result = await rt.native('home/dialog_text', args, reconcile=False)
            readback = await rt.game.invoke('home/dialog_text', {'list': True, 'dryRun': True})
            assert next(f['before'] for f in readback['fields'] if f['name'] == 'curName') == 'Fixture Tribe', readback
            opened = [{'type': fields['window']}]
            status = await rt.game.query('home/status', colonists=False, threats=False)
            assert status['time']['paused'] is True, status
            report = {'field_written': 'curName', 'window': opened[0], 'paused': True, 'result': result}
            (root/'result.json').write_text(json.dumps(report, indent=2))
            print(json.dumps({'field_written': 'curName', 'window': opened[0]['type'], 'paused': True, 'report': str(root/'result.json')}))
        finally:
            await rt.halt()
            await rt.router.close()
            store.close()


if __name__ == '__main__':
    import sys
    asyncio.run(main('--rendered' in sys.argv))

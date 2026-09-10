"""Verify native notification, inspect-tab and exact target reads used by companion instruments."""
from rimbot.bridge import gabs_executable
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
    root = isolated_root('.rimbot/bridge', Path('.rimbot') / f'companion-inspection-smoke-{time.time_ns()}')
    config_dir = prepare(root)
    if rendered:
        config_file = config_dir/'config.json'
        config = json.loads(config_file.read_text())
        config['games']['rimbot-trial']['args'] = [a for a in config['games']['rimbot-trial']['args']
                                                  if a not in ('-batchmode', '-nographics')]
        config_file.write_text(json.dumps(config))
    async with bridge_session(gabs_executable(root), config_dir) as bridge:
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
            report = {}
            for tool in ('rimworld/list_messages','rimworld/list_alerts','rimworld/list_inspect_tabs'):
                report[tool] = await rt.inspect_native(tool, {})
                assert report[tool].get('success') is True, report[tool]
            pawns = await rt.game.query('home/list_pawns', colonistsOnly=True)
            pawn = pawns['pawns'][0]['thingId']
            report['map_target'] = await rt.inspect_native('rimworld/get_map_target_info', {'thingId': pawn})
            assert report['map_target'].get('success') is True, report['map_target']
            (root/'result.json').write_text(json.dumps(report, indent=2))
            print(json.dumps({'passed': list(report), 'report': str(root/'result.json')}))
        finally:
            await rt.halt()
            await rt.router.close()
            store.close()


if __name__ == '__main__':
    import sys
    asyncio.run(main('--rendered' in sys.argv))

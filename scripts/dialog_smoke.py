"""Close an inspected native letter dialog and preserve a paused test game."""
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import prepare, isolated_root
from rimbot.store import Store


async def main(rendered=False):
    root = isolated_root('.rimbot/bridge', Path('.rimbot') / f'dialog-smoke-{time.time_ns()}')
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
            await rt.supervisor.change('Normal')
            before = await rt.game.invoke('rimworld/get_ui_state', {})
            letters = await rt.game.invoke('rimworld/list_letters', {'limit': 1000})
            if letters['letters']:
                await rt.native('rimworld/open_letter', {'letterId': letters['letters'][0]['id']}, reconcile=False)
            else:
                # Fixture setup only; arbitrary window opening is not exposed to the model.
                await rt.supervisor.pause_for_dialog()
                await bridge.call('rimworld/open_window_by_type', windowType='RimWorld.Dialog_Options', replaceExisting=False)
            rt.clock_events.extend(await rt.supervisor.poll())
            rt.receive_clock_events()
            assert rt.mode == 'automate', 'Our own dialog incorrectly disabled automation'
            targets = await rt.game.invoke('rimworld/get_screen_targets', {})
            old = {(w['id'], w['type']) for w in before['windows']}
            opened = [w for w in targets['targets']['windows']
                      if (w['id'], w['type']) not in old and w.get('dismissTargetId')]
            assert len(opened) == 1, targets
            if rendered:
                from rimbot.bridge_game import for_model
                layout = await rt.inspect_native('rimworld/get_ui_layout', {'surfaceId': opened[0]['windowTargetId'], 'timeoutMs': 10000})
                compact = for_model(layout, 'rimworld/get_ui_layout')
                assert compact.get('surfaces'), compact
                (root/'ui-layout.json').write_text(json.dumps({'native': layout, 'compact': compact}, indent=2))
            if rendered:
                buttons = [e for s in layout['surfaces'] for e in s['elements']
                           if e.get('actionable') and e.get('label') in ('OK', 'Close')]
                assert len(buttons) == 1, buttons
                result = await rt.native('rimworld/click_ui_target', {'targetId': buttons[0]['targetId']}, reconcile=False)
                assert all(w['id'] != opened[0]['id'] for w in result['observed_after']['windows']), result
            else:
                result = await rt.native('rimworld/click_screen_target',
                                         {'targetId': opened[0]['dismissTargetId']}, reconcile=False)
            status = await rt.game.query('home/status', colonists=False, threats=False)
            assert status['time']['paused'] is True, status
            assert rt.mode == 'automate'
            assert rt.supervisor.hold is None
            report = {'closed': opened[0], 'paused': True, 'automation_preserved': True, 'result': result}
            (root/'result.json').write_text(json.dumps(report, indent=2))
            print(json.dumps({'closed': opened[0]['type'], 'paused': True, 'report': str(root/'result.json')}))
        finally:
            await rt.halt()
            await rt.router.close()
            store.close()


if __name__ == '__main__':
    import sys
    asyncio.run(main('--rendered' in sys.argv))

"""An externally launched private game survives controller-only checkpoint restart."""
import argparse
import asyncio
import json
from pathlib import Path
import subprocess

from rimgovernor.bridge import bridge_session, gabs_executable
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.headless import prepare
from rimgovernor.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimgovernor.store import Store
from session_checkpoint_acceptance import ready


def fingerprint(pid):
    # Linux /proc field 22, after removing the parenthesized executable label.
    value = Path(f'/proc/{pid}/stat').read_text()
    fields = value[value.rfind(')')+2:].split()
    if fields[0] == 'Z': raise ValueError('Game became a zombie')
    return int(fields[19])


async def main(args):
    root = args.root.resolve()
    config = prepare(root)
    executable = gabs_executable(root)
    report = {}
    rt = resumed = None
    stores = []
    try:
        # The CLI owns the launch; neither controller below may stop its game.
        launched = await asyncio.to_thread(subprocess.run,
            [str(executable), 'games', 'start', 'rimgovernor-trial', '--configDir', str(config)],
            capture_output=True, text=True, timeout=120, check=True)
        (root/'external-launch.log').write_text(launched.stdout+launched.stderr)
        async with bridge_session(executable, config) as bridge:
            await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                              readiness='visual', timeoutMs=90000)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            status = (await bridge.core('games_status', gameId=bridge.game_id)).structuredContent
            from migrate_legacy_session import read_game_claim
            claim = read_game_claim(config, status['diagnostics']['runtime'])
            pid, birth = claim['gamePid'], claim['pidStartTime']
            assert birth == fingerprint(pid)
            report.update(game_pid=pid, game_birth=birth)
        store = Store(root/'external.sqlite'); stores.append(store)
        rt = BridgeRuntime(store, root, fresh=False, headless=True)
        await ready(rt)
        rt.reply('Preserve this external colony conversation.')
        checkpoint = await create_checkpoint(rt, rt.context_token)
        token, chat, plan = rt.context_token, list(rt.chat), rt.current_plan.model_dump()
        assert not (await stop_for_restart(rt, token, checkpoint['manifest_path']))['game_stopped']
        await rt.stop(); rt = None
        assert fingerprint(pid) == birth
        data, state = prepare_resume(checkpoint['manifest_path'])
        store = Store(state/'bridge.sqlite'); stores.append(store)
        resumed = BridgeRuntime(store, root, fresh=False, headless=True, resume=checkpoint['manifest_path'])
        await ready(resumed)
        assert resumed.context_token == token and resumed.chat == chat
        assert resumed.current_plan.model_dump() == plan
        assert resumed.mode == 'manual' and resumed.batch.summary.end_tick == data['tick']
        assert fingerprint(pid) == birth and resumed.counters['model_calls'] == 0
        # A controller-only restart cannot silently load an old native save.
        closing = await create_checkpoint(resumed, token)
        await stop_for_restart(resumed, token, closing['manifest_path'])
        await resumed.stop(); resumed = None
        async with bridge_session(executable, config) as bridge:
            await bridge.connect()
            await bridge.call('rimworld/set_time_speed', speed='Normal', ultraSpeedBoost=False)
            await asyncio.sleep(1)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            changed = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent['time']['ticksGame']
            assert changed > data['tick']
        _, state = prepare_resume(checkpoint['manifest_path'])
        store = Store(state/'bridge.sqlite'); stores.append(store)
        resumed = BridgeRuntime(store, root, fresh=False, headless=True, resume=checkpoint['manifest_path'])
        try:
            await ready(resumed)
        except RuntimeError as error:
            assert 'Attached game changed' in str(error)
        else:
            raise AssertionError('Stale external checkpoint was accepted')
        await resumed.stop(); resumed = None
        assert fingerprint(pid) == birth
        report.update(outcome='PASS', checkpoint=checkpoint, preserved_token=token, refused_changed_tick=changed)
    except BaseException as error:
        report.update(outcome='FAIL', error=str(error))
        raise
    finally:
        for running in (rt, resumed):
            if running is not None: await running.stop()
        for store in stores: store.close()
        # Only the acceptance launcher performs explicit game cleanup.
        async with bridge_session(executable, config) as bridge:
            await bridge.core('games_stop', gameId=bridge.game_id)
        (root/'attached-report.json').write_text(json.dumps(report, indent=2))
    print('PASS: external game PID/birth, load, tick and conversation survive controller-only restart; stale resume refused')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    asyncio.run(asyncio.wait_for(main(parser.parse_args()), 480))

"""Real Windows sharing faults against a private GABS runtime and native game.

Uses installed mods read-only. A scripted plan places native-legal fixture
construction; no model or editor tools are involved. Every trial keeps evidence.
"""
import argparse
import asyncio
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import subprocess
import time
import traceback

from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyPlan, Decision, PlanSpec
from rimbot.config import ModelRole
from rimbot.headless import isolated_root, prepare
from rimbot.projects import ProjectBook
from rimbot.store import Store


@contextmanager
def runtime_handle(path, share):
    """A real reader whose Windows share mode denies rename and/or other reads."""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                       wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    close.restype = wintypes.BOOL
    handle = create(str(path), 0x80000000, share, None, 3, 0x80, None)
    if handle == wintypes.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        yield
    finally:
        if not close(handle):
            raise ctypes.WinError(ctypes.get_last_error())


async def main(source, output):
    output.mkdir(parents=True, exist_ok=False)
    report = {'started': time.time(), 'fault_passes': [], 'source_revision':
        subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()}
    def save():
        (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    root = isolated_root(source, output/'worker')
    config = prepare(root)
    executable = root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe'
    report['gabs_sha256'] = hashlib.sha256(executable.read_bytes()).hexdigest()
    store = Store(output/'controller.sqlite')
    rt = None
    try:
        async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', config) as bridge:
            calls = []
            actual_call = bridge.call
            async def traced(tool, **arguments):
                record = {'tool': tool, 'arguments': arguments, 'started': time.time()}
                calls.append(record)
                try:
                    result = await actual_call(tool, **arguments)
                    record['result'] = result.model_dump(mode='json')
                    return result
                except Exception as error:
                    record['error'] = str(error)
                    raise
                finally:
                    (output/'native-calls.json').write_text(json.dumps(calls, indent=2), encoding='utf8')
            bridge.call = traced
            try:
                print('Starting private headless game', flush=True)
                report['start'] = (await bridge.core('games_start', gameId=bridge.game_id)).model_dump(mode='json')
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                rt = BridgeRuntime(store, root)
                rt.bridge, rt.game = bridge, BridgeGame(bridge)
                await rt.sync_identity()
                rt.batch = await observe(rt.game)
                pawn = rt.batch.summary.pawns[0]
                steps = []
                for index, definition in enumerate(('SleepingSpot', 'Wall')):
                    candidates = [(dx, dz) for dz in range(-4, 5) for dx in range(-4, 5)]
                    for dx, dz in candidates:
                        args = dict(defName=definition, x=pawn.position.x+dx,
                                    z=pawn.position.z+dz, rotation='north', dryRun=True)
                        if any(s['action']['placements'][0]['x'] == args['x'] and
                               s['action']['placements'][0]['z'] == args['z'] for s in steps):
                            continue
                        if definition == 'Wall':
                            args['stuff'] = 'WoodLog'
                        preview = await rt.game.invoke('home/place_building', args)
                        if preview.get('canPlace'):
                            placement = {'def_name': definition, 'x': args['x'], 'z': args['z']}
                            if definition == 'Wall':
                                placement['materials'] = ['WoodLog']
                            steps.append({'id': definition, 'title': 'Runtime fault '+definition,
                                'completion_criteria': 'Native built state',
                                'action': {'kind': 'place_buildings', 'placements': [placement]}})
                            break
                    else:
                        raise AssertionError('No legal fixture cell for '+definition)
                decision = Decision(expected_revision=rt.current_plan.revision, disposition='revise',
                    assessment='Runtime reliability fixture', rationale='Verify receipt retention under OS faults',
                    reply='Place the isolated test targets', plan=PlanSpec(steps=steps))
                await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST,
                    expected_token=rt.context_token, expected_revision=rt.chat_revision)
                rt.mode = 'automate'
                await rt.hands.advance(rt)
                before = rt.current_plan.model_dump()
                assert before['progress']['SleepingSpot']['state'] == 'complete', before
                assert before['progress']['Wall']['state'] == 'waiting', before
                receipts = {key: row.issued.copy() for key, row in rt.current_plan.progress.items()}
                initial_projects = rt.projects.dump()
                report.update(before=before, initial_projects=initial_projects)
                runtime = config/bridge.game_id/'runtime.json'
                report['runtime_file'] = str(runtime)
                assert runtime.is_file(), runtime
                # Close diagnostic readers before querying: this GABS rename
                # also fails while a delete-sharing reader holds the target.
                with runtime_handle(runtime, 7):
                    pass
                report['closed_reader_control'] = await rt.game.query('home/status')
                async def target_rows():
                    rows = []
                    for step in rt.current_plan.spec.steps:
                        p = step.action.placements[0]
                        native = await rt.game.query('home/list_buildings', match=p.def_name,
                            x=p.x, z=p.z, radius=1, aggregate=False, playerOnly=True)
                        matches = [row for row in native['buildings'] if row['position'] == {'x': p.x, 'z': p.z}]
                        assert len(matches) == 1, matches
                        rows.append(matches[0])
                    return rows
                report['initial_native'] = await target_rows()
                print('Native construction accepted; injecting Windows runtime handle faults', flush=True)
                for name, share in [('deny_delete', 3), ('delete_shared_reader', 7), ('deny_read_and_delete', 0)]:
                    with runtime_handle(runtime, share):
                        if share == 0:
                            report['exclusive_reader_status'] = (await bridge.core('games_status',
                                gameId=bridge.game_id)).model_dump(mode='json')
                            diagnostic = report['exclusive_reader_status']['structuredContent']['diagnostics']
                            assert diagnostic['code'] == 'runtime-state-error', diagnostic
                            assert 'being used by another process' in diagnostic['runtime']['error'], diagnostic
                        for repeat in range(4):
                            call_start = len(calls)
                            await rt.projects.reconcile(rt.game)
                            rt.reconcile_plan()
                            await rt.hands.advance(rt)
                            state = rt.current_plan.model_dump()
                            record = {'fault': name, 'repeat': repeat, 'state': state,
                                      'native_calls': calls[call_start:]}
                            report['fault_passes'].append(record)
                            save()
                            assert all(row.state == 'waiting' for row in rt.current_plan.progress.values()), record
                            assert all(row.failure and row.failure.code == 'observation_unavailable'
                                       for row in rt.current_plan.progress.values()), record
                            assert all(row.issued == receipts[key] for key,row in rt.current_plan.progress.items()), record
                            assert any('error' in call for call in record['native_calls']), record
                            rt.persist()
                            rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
                            rt.projects = ProjectBook(rt.projects.dump())
                    await rt.projects.reconcile(rt.game)
                    rt.reconcile_plan()
                    await rt.hands.advance(rt)
                    assert rt.current_plan.progress['SleepingSpot'].state == 'complete'
                    assert rt.current_plan.progress['Wall'].state == 'waiting'
                    assert all(row.failure is None for row in rt.current_plan.progress.values())
                    print('Recovered after '+name, flush=True)
                report['final_native'] = await target_rows()
                assert [(r['thingId'], r['status']) for r in report['final_native']] == [
                    (r['thingId'], r['status']) for r in report['initial_native']]
                writes = [call for call in calls if call['tool'] == 'home/place_building'
                          and call['arguments'].get('dryRun') is False]
                assert len(writes) == 2, writes
                assert all(row.issued == receipts[key] for key,row in rt.current_plan.progress.items())
                report.update(passed=True, construction_writes=len(writes), after=rt.current_plan.model_dump())
                save()
            finally:
                # Only this private DirectPath runtime owns this process.
                report['stop'] = (await bridge.core('games_kill', gameId=bridge.game_id)).model_dump(mode='json')
    except BaseException:
        report['failure'] = traceback.format_exc()
        raise
    finally:
        if rt is not None:
            await rt.router.close()
        store.close()
        report['finished'] = time.time()
        save()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(main(args.source.resolve(), args.output.resolve()))

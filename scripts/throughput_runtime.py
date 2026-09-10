"""Measure the production headless control loop with the read-only dashboard sampler."""
import argparse
import asyncio
import json
import os
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import uvicorn

from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge_server import create_app
from rimgovernor.store import Store
from rimgovernor.flight_recorder import recorder
from rimgovernor.bridge_observation import observe
from deterministic_foothold import NoInference


async def verify_pause_race(rt, report):
    # Reserve the existing execution slot while the probe drives native time.
    # Keep scheduler lifecycle and the independent lease watcher intact.
    assert not any(task is not None and not task.done()
                   for task in (rt.review_task, rt.execution_task)), 'Controller work already active'
    rt.execution_task = asyncio.current_task()
    try:
        await _verify_pause_race(rt, report)
    finally:
        rt.execution_task = None
        rt.work_changed.set()


async def _verify_pause_race(rt, report):
    """Delay a real active-status readback until its native window has stopped."""
    from rimgovernor.native_scenario import advance_game
    clock = rt.supervisor
    original = clock.call
    started = None
    async def capture(**arguments):
        nonlocal started
        value = await original(**arguments)
        if arguments.get('op') == 'start':
            started = dict(value)
        return value
    evidence = report['pause_race'] = dict(passed=False,
        scope='Delayed native status readback; actual native pause refusal and unchanged stopped tick')
    try:
        clock.call = capture
        await advance_game(rt, 60, evidence, expected_letters=())
    finally:
        clock.call = original
    assert started and started['active'], 'No actual active window captured'
    before = await original(op='status')
    assert before['stopReason'] == 'tick_budget' and before['epoch'] == started['epoch']
    owner_task = asyncio.current_task()
    delivered, pauses = False, 0
    async def delayed(**arguments):
        nonlocal delivered, pauses
        if asyncio.current_task() is owner_task:
            if arguments.get('op') == 'status' and not delivered:
                delivered = True
                return started
            if arguments.get('op') == 'pause':
                pauses += 1
        return await original(**arguments)
    try:
        clock.call = delayed
        async with rt.lock:
            result = await clock.change('Paused')
    finally:
        clock.call = original
    after = await original(op='status')
    evidence.update(before=before, after=after, result=result, pause_attempts=pauses)
    assert pauses == 1 and result.get('pauseReconciled') is True
    assert after['lastTick'] == before['lastTick'] and after['paused'] and after['pauseVerified']
    assert after['owner'] == before['owner'] and after['epoch'] == before['epoch']
    evidence['passed'] = True
    rt.batch = await observe(rt.game)
    report['initial_tick'] = rt.batch.summary.end_tick


async def run(args):
    root = Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    prefs_path = root/'profile/Config/Prefs.xml'
    prefs = ET.parse(prefs_path)
    pause = prefs.getroot().find('pauseOnLoad')
    if pause is None:
        pause = ET.SubElement(prefs.getroot(), 'pauseOnLoad')
    pause.text = 'True'
    prefs.write(prefs_path, encoding='utf8', xml_declaration=True)
    store = Store(root/'throughput.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(measured=False, accelerated=args.accelerated, calls=[], bridge_calls=[],
        inputs=json.loads((root/'inputs.json').read_text()),
        scope='Production deterministic controller wall throughput including pauses; no inference or survival claim')
    server = uvicorn.Server(uvicorn.Config(create_app(rt), host='127.0.0.1', port=8790, log_level='warning'))
    server_task = asyncio.create_task(server.serve())
    began = time.perf_counter()
    sampler = None
    recording = True
    profile = None
    milestones = None
    milestone_task = None
    try:
        async with asyncio.timeout(180):
            while not rt.connected or not server.started:
                if rt.phase == 'Connection failed' or server_task.done():
                    raise RuntimeError(rt.phase)
                await asyncio.sleep(.25)
        report['startup_seconds'] = time.perf_counter()-began
        report['initial_tick'] = rt.batch.summary.end_tick
        if getattr(args, 'pause_race', False):
            await verify_pause_race(rt, report)
        rt.bridge.timing_callback = report['bridge_calls'].append
        if args.shell_comparison:
            from shell_preflight_acceptance import compare_shell_preflight
            report['shell_comparison'] = await compare_shell_preflight(rt)
            report['bridge_calls'].clear()
        if args.search_comparison:
            from search_preview_acceptance import compare_search_previews
            report['search_comparison'] = await compare_search_previews(rt)
            report['bridge_calls'].clear()
        if args.placement_comparison:
            from placement_preview_acceptance import compare_placement_previews
            report['placement_comparison'] = await compare_placement_previews(rt)
            report['bridge_calls'].clear()
        if args.observation_comparison:
            report['observation_comparison'] = []
            for repeat in range(4):
                previous = None
                for batched in ((False, True) if repeat % 2 == 0 else (True, False)):
                    rt.game.batch_observations = batched
                    first_call = len(report['bridge_calls'])
                    started = time.perf_counter()
                    batch = await observe(rt.game)
                    assert batch.summary.paused and batch.summary.same_tick, 'Comparison requires paused native state'
                    current = batch.summary.model_dump(mode='json')
                    if previous is not None:
                        assert current == previous, 'Batched observation changed projected native facts'
                    previous = current
                    report['observation_comparison'].append(dict(repeat=repeat, batched=batched,
                        seconds=time.perf_counter()-started, summary=current,
                        bridge_calls=report['bridge_calls'][first_call:]))
            report['bridge_calls'].clear()
        rt.game.batch_observations = not args.unbatched_observations
        report['batched_observations'] = rt.game.batch_observations
        original = rt.bridge.call

        async def measured_call(name, **arguments):
            started = time.perf_counter()
            entry = dict(tool=name, success=False)
            entry['phase'] = ('identity' if name == 'home/colony_identity' else
                              'preview' if arguments.get('dryRun') is True or name == 'home/placement_previews' else
                              'clock' if name == 'home/supervised_play' else
                              'dispatch' if arguments.get('dryRun') is False else 'read')
            try:
                result = await original(name, **arguments)
                entry['success'] = True
                if isinstance(result.structuredContent, dict):
                    operation = result.structuredContent.get('operation')
                    if isinstance(operation, dict):
                        entry['native_ms'] = operation.get('DurationMs')
                return result
            except Exception as error:
                entry['error'] = repr(error)
                raise
            finally:
                entry['seconds'] = time.perf_counter()-started
                if recording:
                    report['calls'].append(entry)

        rt.bridge.call = measured_call
        rt.supervisor.test_acceleration = args.accelerated
        if args.profile_controller:
            from controller_profile import ControllerProfile
            profile = ControllerProfile(rt, root)
            profile.start()
        rt.current_plan.control.setdefault('policy', {})['execution_speed'] = 'Superfast'
        from startup_milestones import StartupMilestones
        milestones = StartupMilestones(rt)
        await rt.set_mode('automate')
        async def sample_milestones():
            while True:
                milestones.sample(rt)
                await asyncio.sleep(.1)
        milestone_task = asyncio.create_task(sample_milestones())
        with (root/'dashboard-sampler.log').open('w', encoding='utf8') as log:
            sampler = await asyncio.create_subprocess_exec(sys.executable, 'scripts/dashboard_throughput.py',
                '--port', '8790', '--seconds', str(args.seconds), '--interval', '1',
                '--output', str(root/'dashboard-throughput'), stdout=log, stderr=asyncio.subprocess.STDOUT)
            async with asyncio.timeout(args.seconds+30):
                assert await sampler.wait() == 0, 'Read-only sampler failed; inspect dashboard-sampler.log'
        sampled = json.loads((root/'dashboard-throughput/result.json').read_text())
        report['dashboard'] = sampled['summary']
        assert report['dashboard']['valid_seconds'] >= args.seconds*.8, 'Insufficient connected sampling time'
        assert not any(row.get('error') for row in sampled['samples']), 'Dashboard read failed'
        report['final_tick'] = rt.batch.summary.end_tick
        report['controller_status'] = rt.current_plan.control.get('status')
        report['criteria'] = rt.current_plan.control.get('criteria')
        report['plan'] = rt.current_plan.model_dump(mode='json')
        report['events'] = store.history(rt.colony, limit=10000, include_diagnostics=True)
        report['model_attempts'] = NoInference.attempts
        assert report['model_attempts'] == 0, 'Deterministic throughput must not attempt inference'
        if getattr(args, 'pause_race', False):
            milestones.sample(rt)
            assert milestones.valid and 'first_observed_pawn_work' in milestones.rows, 'No observed native pawn work'
            assert not any(str(e.get('text', '')).startswith(('Review stopped:', 'Execution stopped:'))
                           for e in report['events']), 'Controller stopped; inspect retained events'
        report['measured'] = True
    except BaseException as error:
        report['measured'] = False
        report['error'] = repr(error)
        raise
    finally:
        if milestone_task:
            milestone_task.cancel()
            await asyncio.gather(milestone_task, return_exceptions=True)
        if milestones:
            milestones.sample(rt)
            report['startup_milestones'] = milestones.report()
        if profile is not None:
            report['controller_profile'] = profile.stop()
        recording = False
        if getattr(rt, 'bridge', None):
            rt.bridge.timing_callback = None
        if sampler is not None and sampler.returncode is None:
            sampler.terminate()
            await sampler.wait()
        server.should_exit = True
        report['total_seconds'] = time.perf_counter()-began
        report['phases'] = {phase: dict(calls=len(rows), seconds=sum(row['seconds'] for row in rows))
                           for phase in sorted({row['phase'] for row in report['calls']})
                           if (rows := [row for row in report['calls'] if row['phase'] == phase])}
        (root/'runtime-throughput.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        try:
            await asyncio.wait_for(server_task, timeout=90)
        except BaseException as error:
            report['cleanup_error'] = repr(error)
            report['measured'] = False
            raise
        finally:
            report['total_seconds'] = time.perf_counter()-began
            report['flight_recorder'] = recorder().stats() if recorder() else None
            (root/'runtime-throughput.json').write_text(json.dumps(report, indent=2), encoding='utf8')
            store.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--accelerated', action='store_true')
    parser.add_argument('--pause-race', action='store_true', help='Verify delayed-status/native tick-stop pause reconciliation before sampling')
    parser.add_argument('--profile-controller', action='store_true', help='Retain Python CPU and nested controller wall timings')
    parser.add_argument('--seconds', type=int, default=120)
    parser.add_argument('--unbatched-observations', action='store_true', help='Compare the legacy seven-call observation path')
    parser.add_argument('--observation-comparison', action='store_true', help='Verify paired paused native observations before the runtime sample')
    parser.add_argument('--placement-comparison', action='store_true', help='Verify bounded placement batches against individual native previews')
    parser.add_argument('--search-comparison', action='store_true', help='Verify native site search and material alternative equivalence')
    parser.add_argument('--shell-comparison', action='store_true', help='Compare read-only Hands shell preflight with shared native reads')
    args = parser.parse_args()
    if not 10 <= args.seconds <= 1800:
        parser.error('Use 10..1800 seconds')
    asyncio.run(run(args))

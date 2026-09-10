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

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_server import create_app
from rimbot.store import Store
from rimbot.flight_recorder import recorder
from rimbot.bridge_observation import observe
from deterministic_foothold import NoInference


async def run(args):
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
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
    try:
        async with asyncio.timeout(180):
            while not rt.connected or not server.started:
                if rt.phase == 'Connection failed' or server_task.done():
                    raise RuntimeError(rt.phase)
                await asyncio.sleep(.25)
        report['startup_seconds'] = time.perf_counter()-began
        report['initial_tick'] = rt.batch.summary.end_tick
        rt.bridge.timing_callback = report['bridge_calls'].append
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
        rt.current_plan.control.setdefault('policy', {})['execution_speed'] = 'Superfast'
        await rt.set_mode('automate')
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
        report['measured'] = True
    except BaseException as error:
        report['measured'] = False
        report['error'] = repr(error)
        raise
    finally:
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
    parser.add_argument('--seconds', type=int, default=120)
    parser.add_argument('--unbatched-observations', action='store_true', help='Compare the legacy seven-call observation path')
    parser.add_argument('--observation-comparison', action='store_true', help='Verify paired paused native observations before the runtime sample')
    parser.add_argument('--placement-comparison', action='store_true', help='Verify bounded placement batches against individual native previews')
    args = parser.parse_args()
    if not 10 <= args.seconds <= 1800:
        parser.error('Use 10..1800 seconds')
    asyncio.run(run(args))

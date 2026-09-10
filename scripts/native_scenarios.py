"""List, run and inspect resource-bounded native Docker scenarios."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import re
from pathlib import Path
import shutil
import sqlite3
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET

from container_checks import docker_environment
from container_scenario import dashboard_options, require_dashboard_image


SCENARIOS = {
    'rendered-input': dict(script='native_scenario_view.py', flags=['--input-probe', '--extended-input', '--seconds', '60'],
                          requires=['rendered native video, selection, main-tab and camera schemas'], displays=['xvfb'],
                          rendered_flag=False),
    'video-input': dict(script='native_scenario_view.py', flags=['--input-probe', '--seconds', '60'],
                       requires=['rendered native video and selection schemas'], displays=['xvfb'],
                       rendered_flag=False),
    'construction': dict(script='native_scenario_probe.py', flags=['--case', 'construction'],
                         requires=['legal wall site', 'accessible WoodLog', 'capable builder']),
    'blocked-construction': dict(script='native_scenario_probe.py', flags=['--case', 'blocked-construction'],
                                requires=['legal wall site', 'accessible WoodLog', 'capable builder']),
    'production': dict(script='resource_policy_acceptance.py', flags=[],
                       requires=['accessible WoodLog for two native recipes', 'capable crafter']),
    'acquisition': dict(script='resource_policy_acceptance.py', flags=['--acquisition'],
                        requires=['native mineable steel/components', 'ripe herbal medicine', 'capable workers']),
    'paired-restart': dict(script='session_checkpoint_acceptance.py', flags=['--mixed', '--timeout', '900'],
                           requires=['ordinary colony with construction stock and growing space']),
    'assertion-failure': dict(script='native_scenario_probe.py', flags=['--case', 'assertion-failure'], requires=[]),
    'timeout': dict(script='native_scenario_probe.py', flags=['--case', 'timeout'], requires=[]),
    'native-exit': dict(script='native_scenario_probe.py', flags=['--case', 'native-exit'], requires=[]),
    'startup': dict(script='native_scenario_probe.py', flags=['--case', 'startup'], requires=[]),
    'endurance': dict(script='native_scenario_probe.py', flags=['--case', 'endurance', '--seconds', '1800'],
                      requires=['ordinary colony supporting three native days'],
                      scope='Native clock and process endurance; does not certify colony survival or pawn work'),
    'treatment': dict(script='native_combat_smoke.py', flags=['--tend', '--recovery'],
                      requires=['ordinary reachable wild animal', 'capable doctor', 'combat produces a treatable wound'],
                      displays=['headless']),
    'scarce-supplies': dict(script='construction_recovery_acceptance.py', flags=[], displays=['headless'],
                            requires=['ordinary construction stock and two legal wall cells'],
                            scope='Prewrite stock refusal and recovery; no pawn completion claim'),
    'placement-obstruction': dict(script='construction_recovery_acceptance.py', flags=['--obstruction'],
                                 displays=['headless'], requires=['two legal campfire sites, WoodLog and capable builders'],
                                 scope='Placement obstruction recovery; not pawn pathfinding acceptance'),
    'player-edits': dict(script='project_postconditions_acceptance.py', flags=[], displays=['headless'],
                         requires=['ordinary growing-zone and sleeping-spot sites']),
    'queued-construction': dict(script='project_scheduling_acceptance.py', flags=[], displays=['headless'],
                                requires=['accessible stock and capable builders', 'companion built with ConstructionLedgerFixture=true'],
                                scope='Scarce construction scheduling and actual dependent pawn completion'),
    'projected-access': dict(script='spatial_site_acceptance.py', flags=['--projected','--seconds','600'],
                            displays=['headless'], requires=['native spatial-access schema and legal shelter site'],
                            scope='Native prewrite projected route obstruction; no already-trapped pawn claim'),
}

for _variant in ('recovery', 'pause', 'modal', 'raid', 'load'):
    SCENARIOS['warning-'+_variant] = dict(script='native_scenario_probe.py',
        flags=['--case', 'warning-'+_variant], requires=['private InterruptionFixtures assembly'],
        displays=['headless', 'xvfb'], scope='Shared wait native warning recovery or refusal; pause uses a native player-equivalent call')

SCENARIOS['checkpoint-continuation'] = dict(script='native_scenario_probe.py',
    flags=['--case', 'checkpoint-continuation'], requires=['--checkpoint: immutable owned named-runner checkpoint with issued construction'],
    scope='Targeted pending construction continuation; not fresh-start acceptance')
SCENARIOS['construction-checkpoint'] = dict(script='native_scenario_probe.py',
    flags=['--case', 'construction', '--checkpoint-before-work'], requires=['ordinary wall construction fixture'],
    scope='Fresh construction with an immutable paired checkpoint before pawn work')
SCENARIOS['plant-acquisition'] = dict(script='resource_policy_acceptance.py',
    flags=['--acquisition', '--acquisition-resources', 'MedicineHerbal', 'WoodLog'],
    requires=['reachable mature wild plants', 'capable plant workers'], scope='Actual herbal medicine and wood stock; no mining claim')
SCENARIOS['mining'] = dict(script='mining_acceptance.py', flags=['--seconds', '900'],
    requires=['private companion built with MiningFixture=true'], displays=['headless'],
    scope='Disposable surface geometry; native mining, hauling, roof refusal, cancellation and pending dig restart')


def backup_databases(root):
    results = []
    for source in list(root.rglob('*.sqlite')):
        if 'failure-bundle' in source.parts:
            continue
        destination = root/'failure-bundle'/source.relative_to(root)
        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            with sqlite3.connect(source.as_uri()+'?mode=ro', uri=True, timeout=5) as connection:
                with sqlite3.connect(destination) as backup:
                    connection.backup(backup)
            results.append(dict(source=str(source.relative_to(root)), backup=str(destination.relative_to(root))))
        except sqlite3.Error as error:
            results.append(dict(source=str(source.relative_to(root)), error=str(error)))
    return results


def resource_summary(root):
    samples, cpu, memory = 0, [], []
    path = root/'resources.jsonl'
    for line in path.read_text(encoding='utf8').splitlines() if path.exists() else []:
        try:
            envelope = json.loads(line)
            row = json.loads(envelope.get('sample') or '{}')
            if envelope.get('exit_code') != 0 or not row:
                continue
            samples += 1
            cpu.append(float(row['CPUPerc'].rstrip('%')))
            value = re.fullmatch(r'([\d.]+)\s*(B|kB|MB|GB|KiB|MiB|GiB)', row['MemUsage'].split('/')[0].strip())
            if value:
                scale = {'B':1,'kB':1000,'MB':1000**2,'GB':1000**3,'KiB':1024,'MiB':1024**2,'GiB':1024**3}
                memory.append(round(float(value[1])*scale[value[2]]))
        except (ValueError, KeyError, TypeError):
            continue
    return dict(samples=samples, mean_sampled_cpu_percent=sum(cpu)/len(cpu) if cpu else None,
                peak_sampled_memory_bytes=max(memory) if memory else None,
                scope='Ten-second container samples under shared host load; not a GC-pause profiler or isolated benchmark')


def classify(code, root, timed_out=False, scenario=None):
    if timed_out:
        return 'timeout'
    result = root/'scenario'/'scenario-result.json'
    if result.exists():
        outcome = json.loads(result.read_text())
        return outcome['category'] if code == 0 or outcome['category'] != 'passed' else 'infrastructure_failure'
    for filename in ('result.json', 'report.json'):
        evidence = root/'scenario'/filename
        if evidence.exists() and json.loads(evidence.read_text()).get('outcome') == 'missing_prerequisite':
            return 'missing_prerequisite'
    if code == 0:
        if scenario == 'mining':
            evidence = root/'scenario/result.json'
            if evidence.exists():
                outcome = json.loads(evidence.read_text())
                if outcome.get('passed') is True and outcome.get('cases') and all(c.get('passed') is True for c in outcome['cases']):
                    return 'passed'
        if scenario == 'treatment':
            evidence = root/'scenario/medical-smoke.json'
            if evidence.exists():
                outcome = json.loads(evidence.read_text())
                if outcome.get('patient_after') and outcome.get('tend_plan_completed', {}).get('state')=='complete':
                    return 'passed'
        for filename in ('result.json', 'report.json'):
            evidence = root/'scenario'/filename
            if evidence.exists() and json.loads(evidence.read_text()).get('outcome') in ('passed', 'PASS'):
                return 'passed'
        return 'infrastructure_failure'
    text = (root/'container.log').read_text(encoding='utf8', errors='replace')
    if 'AssertionError' in text:
        return 'assertion_failure'
    if 'TimeoutError' in text:
        return 'timeout'
    return 'infrastructure_failure'


def junit(results, destination):
    suite = ET.Element('testsuite', name='native-scenarios', tests=str(len(results)),
                       failures=str(sum(r['category'] != 'passed' for r in results)))
    for row in results:
        case = ET.SubElement(suite, 'testcase', name=row['scenario']+'-'+str(row['attempt']),
                             time=str(row['seconds']))
        if row['category'] != 'passed':
            ET.SubElement(case, 'failure', type=row['category'], message=row.get('error') or row['category']).text = json.dumps(row)
    ET.ElementTree(suite).write(destination, encoding='utf8', xml_declaration=True)


def run(args):
    source = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    def preflight_failure(category, error):
        rows = [dict(scenario='preflight', attempt=1, seconds=0, category=category, error=str(error))]
        (output/'result.json').write_text(json.dumps(dict(passed=False, results=rows), indent=2))
        junit(rows, output/'junit.xml')
        return False
    try:
        docker, env = docker_environment()
    except (ValueError, OSError) as error:
        return preflight_failure('infrastructure_failure', error)
    def command(*parts, **kwargs):
        return subprocess.run([docker, *parts], env=env, cwd=source, **kwargs)
    for name, relative in [('game', 'RimWorldLinux'), ('mods', ''), ('profile', 'Saves/RimBot-tribal8-baseline.rws'), ('gabs', 'gabs')]:
        if not (getattr(args, name)/relative).exists():
            return preflight_failure('missing_prerequisite', f'Missing required {name} input: {getattr(args, name)/relative}')
    for scenario in args.scenario:
        if (scenario == 'checkpoint-continuation') != bool(getattr(args, 'checkpoint', None)):
            return preflight_failure('missing_prerequisite', '--checkpoint is required exclusively for checkpoint-continuation')
        if args.display not in SCENARIOS[scenario].get('displays', ['headless', 'xvfb']):
            return preflight_failure('missing_prerequisite', f'{scenario} does not support {args.display}')
    if getattr(args, 'checkpoint', None) and not all((args.checkpoint/name).is_file()
            for name in ('checkpoint.json', 'game.rws', 'bridge.sqlite')):
        return preflight_failure('missing_prerequisite', 'Checkpoint directory must contain the complete immutable pair and manifest')
    if not args.no_build:
        with (output/'build.log').open('w', encoding='utf8') as log:
            try:
                command('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                        stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
            except (subprocess.SubprocessError, OSError) as error:
                return preflight_failure('infrastructure_failure', error)
    try:
        image = command('image', 'inspect', '--format', '{{.Id}}', args.image, capture_output=True, text=True, check=True, timeout=30).stdout.strip()
        require_dashboard_image(command, image)
        packaged = command('run', '--rm', '--entrypoint', 'python', image, '-c',
            "import hashlib,json;from pathlib import Path;root=Path('/app');print(json.dumps({str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for d in ('controller','scripts','integrations') for p in sorted((root/d).rglob('*')) if p.is_file() and p.suffix in ('.py','.cs','.csproj')}))",
            capture_output=True, text=True, check=True, timeout=60)
        packaged_hashes = json.loads(packaged.stdout)
        revision = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=source, capture_output=True, text=True, check=True).stdout.strip()
        source_files = subprocess.run(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=source,
                                      capture_output=True, text=True, check=True).stdout.split('\0')
        hashes = {p: hashlib.sha256((source/p).read_bytes()).hexdigest() for p in sorted(set(source_files)) if p and (source/p).is_file()}
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return preflight_failure('infrastructure_failure', error)
    manifest = dict(version=1, image=image, checkout_revision=revision, checkout_source_hashes=hashes,
                    packaged_source_hashes=packaged_hashes,
                    parameters={k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items()},
                    inference='scripted; no model calls', start='fresh baseline per attempt',
                    rerun_argv=['python', 'scripts/native_scenarios.py', '--scenario', *args.scenario,
                        '--game', str(args.game.resolve()), '--mods', str(args.mods.resolve()),
                        '--profile', str(args.profile.resolve()), '--gabs', str(args.gabs.resolve()),
                        '--image', image, '--no-build', '--output', '<new-output-directory>',
                        '--repeat', str(args.repeat), '--workers', str(args.workers), '--timeout', str(args.timeout),
                        '--memory', args.memory, '--cpus', str(args.cpus), '--gc', args.gc])
    manifest['rerun_argv'] += ['--display', args.display]
    manifest['rerun_argv'] += ['--recording', args.recording]
    manifest['rerun_argv'] += ['--gabs-log-level', args.gabs_log_level]
    manifest['rerun_argv'] += ['--storage', args.storage]
    if getattr(args, 'checkpoint', None):
        manifest['start'] = 'immutable paired checkpoint continuation'
        manifest['checkpoint_inputs'] = {name: hashlib.sha256((args.checkpoint/name).read_bytes()).hexdigest()
                                        for name in ('checkpoint.json', 'game.rws', 'bridge.sqlite')}
        manifest['rerun_argv'] += ['--checkpoint', str(args.checkpoint.resolve())]
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2))
    def trial(item):
        scenario, attempt = item
        root = output/f'{scenario}-{attempt}'
        root.mkdir()
        name = 'rimbot-scenario-'+uuid.uuid4().hex[:12]
        row = dict(scenario=scenario, attempt=attempt, container=name, category='infrastructure_failure', cleanup=False,
                   recording=args.recording, storage=args.storage)
        volume = name+'-work' if args.storage == 'volume' else None
        volume_created = False
        if volume:
            row['volume'] = volume
        (root/'trial.json').write_text(json.dumps(row, indent=2))
        began = time.monotonic()
        timed_out = False
        sampling_done = threading.Event()
        def sample():
            with (root/'resources.jsonl').open('w', encoding='utf8') as stream:
                while not sampling_done.wait(10):
                    try:
                        usage = command('stats', '--no-stream', '--format', '{{json .}}', name,
                                        capture_output=True, text=True, timeout=10)
                        stream.write(json.dumps(dict(wall_time=time.time(), sample=usage.stdout,
                                                    error=usage.stderr, exit_code=usage.returncode))+'\n')
                        stream.flush()
                    except Exception as error:
                        stream.write(json.dumps(dict(wall_time=time.time(), error=repr(error)))+'\n')
                        stream.flush()
        sampler = threading.Thread(target=sample)
        sampler.start()
        try:
            if volume:
                command('volume', 'create', '--label', 'rimbot.scenario='+name, volume,
                        capture_output=True, text=True, check=True, timeout=30)
                volume_created = True
            cmd = ['run', '--init', '--name', name, '--memory', args.memory, '--cpus', str(args.cpus),
                   '-e', 'RIMBOT_UNITY_GC_TIME_SLICE='+args.gc,
                   '-e', 'RIMBOT_DISPLAY='+args.display,
                   '-e', 'RIMBOT_GABS_LOG_LEVEL='+args.gabs_log_level,
                   '-e', 'RIMBOT_RUN_ID='+name,
                   '-e', 'RIMBOT_SCENARIO='+scenario]
            cmd += dashboard_options(scenario+' '+str(attempt), args.display)
            if args.recording == 'on':
                cmd += ['-e', 'RIMBOT_FLIGHT_RECORDER=/worker/timeline.jsonl']
            for key in ('game', 'mods', 'profile', 'gabs'):
                cmd += ['--mount', f'type=bind,source={getattr(args, key).resolve()},target=/inputs/{key},readonly']
            storage = f'type=volume,source={volume},target=/worker' if volume else f'type=bind,source={root},target=/worker'
            if getattr(args, 'checkpoint', None):
                cmd += ['--mount', f'type=bind,source={args.checkpoint.resolve()},target=/inputs/checkpoint,readonly']
            cmd += ['--mount', storage, image,
                    '--', 'python', '/app/scripts/'+SCENARIOS[scenario]['script'],
                    '--source-root', '/worker/run', '--output', '/worker/scenario', *SCENARIOS[scenario]['flags']]
            if getattr(args, 'checkpoint', None):
                cmd += ['--checkpoint', '/inputs/checkpoint']
            if args.display == 'xvfb' and SCENARIOS[scenario].get('rendered_flag', True):
                cmd += ['--rendered']
            with (root/'container.log').open('w', encoding='utf8') as log:
                try:
                    result = command(*cmd, stdout=log, stderr=subprocess.STDOUT, timeout=args.timeout)
                    row['exit_code'] = result.returncode
                except subprocess.TimeoutExpired:
                    timed_out = True
                    row['exit_code'] = None
                    row['error'] = f'Exceeded {args.timeout}s wall budget'
        except Exception as error:
            row['error'] = repr(error)
        finally:
            sampling_done.set()
            sampler.join(timeout=15)
            # Stop owned processes before the SQLite backup so all files form a stable failure window.
            try:
                stop = command('stop', '--time', '30', name, capture_output=True, text=True, timeout=45)
                (root/'stop.log').write_text(stop.stdout+stop.stderr)
                inspection = command('inspect', name, capture_output=True, text=True, timeout=15)
                (root/'container-inspect.json').write_text(inspection.stdout or inspection.stderr)
                if inspection.returncode == 0:
                    state = json.loads(inspection.stdout)[0]['State']
                    row['container_state'] = state
                    if state.get('Running'):
                        command('kill', name, capture_output=True, text=True, check=True, timeout=20)
                        command('wait', name, capture_output=True, text=True, check=True, timeout=30)
                        row['forced_cleanup_stop'] = True
                    row['stopped'] = True
                else:
                    raise RuntimeError('Owned container state could not be inspected')
            except Exception as error:
                row['stop_error'] = repr(error)
                row['category'] = 'infrastructure_failure'
                try:
                    command('kill', name, capture_output=True, text=True, check=True, timeout=20)
                    command('wait', name, capture_output=True, text=True, check=True, timeout=30)
                    row['stopped'] = True
                except Exception as kill_error:
                    row['kill_error'] = repr(kill_error)
            if volume_created and row.get('stopped'):
                exported = time.monotonic()
                try:
                    copied = command('cp', name+':/worker/.', str(root), capture_output=True, text=True, check=True, timeout=180)
                    (root/'export.log').write_text(copied.stdout+copied.stderr)
                    row['exported'] = True
                except Exception as error:
                    row['export_error'] = repr(error)
                    row['volume_retained'] = True
                row['export_seconds'] = time.monotonic()-exported
            elif volume_created:
                row['export_error'] = 'Container stop was not confirmed; retain the volume for recovery'
                row['volume_retained'] = True
            if 'exit_code' in row and not row.get('stop_error') and not row.get('export_error'):
                row['category'] = classify(row['exit_code'], root, timed_out, scenario)
            if row.get('container_state', {}).get('OOMKilled') or row.get('forced_cleanup_stop'):
                row['category'] = 'infrastructure_failure'
            row['database_backups'] = backup_databases(root)
            try:
                from inspect_native_failure import concise_summary
                (root/'diagnosis.json').write_text(json.dumps(concise_summary(root), indent=2))
                row['diagnosis'] = 'diagnosis.json'
            except Exception as error:
                row['diagnosis_error'] = repr(error)
            try:
                cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
                (root/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr)
                row['cleanup'] = cleanup.returncode == 0
                if volume_created and row.get('exported') and row['cleanup']:
                    removed = command('volume', 'rm', volume, capture_output=True, text=True, timeout=30)
                    (root/'volume-cleanup.log').write_text(removed.stdout+removed.stderr)
                    row['volume_retained'] = removed.returncode != 0
                    row['cleanup'] = removed.returncode == 0
                elif volume_created:
                    row['volume_retained'] = True
            except Exception as error:
                row['cleanup_error'] = repr(error)
            if row.get('volume_retained'):
                row['cleanup'] = False
            if not row['cleanup']:
                row['category'] = 'infrastructure_failure'
            row['seconds'] = round(time.monotonic()-began, 3)
            row['resources'] = resource_summary(root)
            row['disk_bytes'] = sum(p.stat().st_size for p in root.rglob('*') if p.is_file())
            row['bundle'] = str(root)
            row['checkpoint_policy'] = 'Probe may save at verified paused boundaries; host never requests a save after an unknown native failure.'
            (root/'result.json').write_text(json.dumps(row, indent=2))
        print(f'{scenario} attempt {attempt}: {row["category"]}', flush=True)
        return row
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        results = list(pool.map(trial, [(s, i) for s in args.scenario for i in range(1, args.repeat+1)]))
    report = dict(passed=all(r['category']=='passed' for r in results), results=results,
                  flakiness={s: dict(attempts=sum(r['scenario']==s for r in results),
                     failures=sum(r['scenario']==s and r['category']!='passed' for r in results)) for s in args.scenario})
    (output/'result.json').write_text(json.dumps(report, indent=2))
    junit(results, output/'junit.xml')
    return report['passed']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--list', action='store_true')
    parser.add_argument('--scenario', nargs='+', choices=SCENARIOS, default=['construction', 'production', 'paired-restart'])
    for key in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--'+key, type=Path)
    parser.add_argument('--image', default='rimbot-worker:native-scenarios')
    parser.add_argument('--checkpoint', type=Path, help='Immutable named-runner checkpoint directory for targeted construction continuation')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--repeat', type=int, choices=range(1, 101), default=1)
    parser.add_argument('--workers', type=int, choices=range(1, 5), default=1)
    parser.add_argument('--memory', default='4g')
    parser.add_argument('--cpus', type=float, default=2)
    parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--gc', choices=['0', 'source'], default='0')
    parser.add_argument('--display', choices=['headless', 'xvfb'], default='headless')
    parser.add_argument('--storage', choices=['volume', 'bind'], default='volume',
                        help='Private Linux volume with final export; bind preserves host-mounted reproduction mode')
    parser.add_argument('--recording', choices=['on', 'off'], default='on', help='Disable only for explicit overhead comparisons')
    parser.add_argument('--gabs-log-level', choices=['debug', 'info', 'warn', 'error'], default='info')
    args = parser.parse_args()
    if args.list:
        print(json.dumps(SCENARIOS, indent=2))
        return 0
    if any(getattr(args, key) is None for key in ('game', 'mods', 'profile', 'gabs', 'output')):
        parser.error('--game, --mods, --profile, --gabs and --output are required')
    if args.cpus <= 0 or args.timeout <= 0:
        parser.error('CPU and timeout budgets must be positive')
    return 0 if run(args) else 1


if __name__ == '__main__':
    raise SystemExit(main())

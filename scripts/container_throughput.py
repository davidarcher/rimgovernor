"""Run B16 clock measurements in private local Docker workers, sequentially."""
import argparse
import json
import hashlib
import shutil
from pathlib import Path
import subprocess
import time
import uuid

from container_checks import docker_environment
from container_input_cache import prepare_cache
from container_scenario import dashboard_options, require_dashboard_image
from worker_storage import storage_options, export_storage, release_storage


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parents[1]
    docker, env = docker_environment()

    def command(*values, **kwargs):
        return subprocess.run([docker, *values], cwd=source, env=env, **kwargs)

    if not args.no_build:
        with (output/'build.log').open('w', encoding='utf8') as stream:
            command('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                    stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=1800)
    image = command('image', 'inspect', '--format', '{{.Id}}', args.image,
                    capture_output=True, text=True, check=True).stdout.strip()
    require_dashboard_image(command, image)
    cache = None if args.no_input_cache else prepare_cache(args.game, args.mods, args.gabs/'gabs', image, output)
    report = dict(passed=False, image=image, workers=[], input_cache=cache, start_type='fresh_baseline', recording=args.recording)
    profile = args.profile.resolve()
    if args.checkpoint:
        profile = output/'profile'
        (profile/'Config').mkdir(parents=True)
        (profile/'Saves').mkdir()
        for filename in ('Prefs.xml', 'ModsConfig.xml'):
            shutil.copy2(args.profile/'Config'/filename, profile/'Config'/filename)
        checkpoint_hash = hashlib.sha256(args.checkpoint.read_bytes()).hexdigest()
        copied = profile/'Saves/RimBot-tribal8-baseline.rws'
        shutil.copy2(args.checkpoint, copied)
        if any(hashlib.sha256(p.read_bytes()).hexdigest() != checkpoint_hash for p in (copied, args.checkpoint)):
            raise ValueError('Checkpoint changed during snapshot preparation')
        report.update(start_type='saved_checkpoint', checkpoint=str(args.checkpoint.resolve()),
                      checkpoint_sha256=checkpoint_hash)
    for mode in args.modes:
        root = output/mode
        root.mkdir()
        name = 'rimbot-b16-'+uuid.uuid4().hex[:12]
        peers = command('ps', '--format', '{{json .}}', capture_output=True, text=True, check=True).stdout
        worker = dict(mode=mode, container=name, other_containers=[json.loads(line) for line in peers.splitlines()], passed=False)
        worker['resource_state_before'] = command('stats', '--no-stream', '--format', '{{json .}}',
            capture_output=True, text=True, timeout=30, check=True).stdout
        report['workers'].append(worker)
        display = 'headless' if mode == 'headless' else 'xvfb'
        invocation = ['run', '-d', '--init', '--name', name, *dashboard_options('B16 throughput '+mode, display)]
        if args.recording == 'on':
            invocation.extend(['-e', 'RIMBOT_FLIGHT_RECORDER=/worker/timeline.jsonl'])
        if cache:
            invocation.extend(['--mount', f"type=volume,source={cache['volume']},target=/cached-inputs,readonly",
                               '-e', 'RIMBOT_INPUT_CACHE_ROOT=/cached-inputs/snapshot',
                               '-e', 'RIMBOT_INPUT_CACHE_KEY='+cache['key']])
        for path, destination in ((args.game, 'game'), (args.mods, 'mods'), (profile, 'profile'), (args.gabs, 'gabs')):
            invocation.extend(['--mount', f'type=bind,source={path.resolve()},target=/inputs/{destination},readonly'])
        if args.runtime_seconds:
            probe = ['python', 'scripts/throughput_runtime.py', '--seconds', str(args.runtime_seconds)]
            if args.runtime_accelerated:
                probe.append('--accelerated')
            if args.runtime_unbatched_observations:
                probe.append('--unbatched-observations')
            if args.observation_comparison:
                probe.append('--observation-comparison')
            if args.placement_comparison:
                probe.append('--placement-comparison')
            if args.search_comparison:
                probe.append('--search-comparison')
            if args.shell_comparison:
                probe.append('--shell-comparison')
            if args.profile_controller:
                probe.append('--profile-controller')
        else:
            probe = ['python', 'scripts/throughput_acceptance.py', '--mode', mode,
                     '--repeats', str(args.repeats), '--ticks', *map(str, args.ticks)]
            if args.fixture:
                probe.append('--fixture')
            if args.checkpoint:
                probe.append('--saved-checkpoint')
        mounts, worker['storage'] = storage_options(command, root, name, args.worker_storage)
        invocation.extend([*mounts, image,
                           '--unity-gc-time-slice', '0', '--display', 'headless' if mode == 'headless' else 'xvfb',
                           '--', *probe])
        worker['command'] = invocation
        started = time.perf_counter()
        try:
            command(*invocation, capture_output=True, text=True, check=True, timeout=120)
            address = command('port', name, '8787/tcp', capture_output=True, text=True, check=True).stdout.strip()
            worker['dashboard_url'] = 'http://'+address+'/scenario'
            (root/'dashboard.json').write_text(json.dumps(worker, indent=2), encoding='utf8')
            print('Scenario dashboard: '+worker['dashboard_url'], flush=True)
            result = command('wait', name, capture_output=True, text=True, check=True, timeout=args.timeout)
            worker['exit_code'] = int(result.stdout.strip())
            worker['scope'] = 'Production-loop measurement, not survival acceptance' if args.runtime_seconds else 'Native bounded outcomes and clock acceptance'
            worker['passed'] = worker['exit_code'] == 0
        except Exception as error:
            worker['error'] = repr(error)
        finally:
            exported = export_storage(command, name, root, worker['storage'])
            worker['passed'] = worker['passed'] and exported
            evidence = root/('run/runtime-throughput.json' if args.runtime_seconds else 'run/throughput-result.json')
            try:
                worker['passed'] = worker['passed'] and evidence.is_file() and json.loads(evidence.read_text()).get(
                    'measured' if args.runtime_seconds else 'passed') is True
            except Exception as error:
                worker.update(passed=False, evidence_error=repr(error))
            try:
                with (root/'container.log').open('w', encoding='utf8') as stream:
                    command('logs', name, stdout=stream, stderr=subprocess.STDOUT, timeout=30)
            except Exception as error:
                worker['log_error'] = repr(error)
                worker['passed'] = False
            if exported:
                cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
                (root/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr, encoding='utf8')
                worker['cleanup_ok'] = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
            else:
                worker['cleanup_ok'] = False
                worker['container_retained'] = True
            worker['passed'] = worker['passed'] and worker['cleanup_ok']
            release_storage(command, worker['storage'], worker['passed'])
            worker['seconds'] = time.perf_counter()-started
            worker['resource_state_after'] = command('stats', '--no-stream', '--format', '{{json .}}',
                capture_output=True, text=True, timeout=30, check=True).stdout
            (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        if not worker['passed']:
            break
    report['passed'] = len(report['workers']) == len(args.modes) and all(w['passed'] for w in report['workers'])
    (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report, indent=2), flush=True)
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--image', default='rimbot-worker:b16')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--no-input-cache', action='store_true')
    parser.add_argument('--recording', choices=['on', 'off'], default='on', help='Match standard native scenarios; disable only for explicit recording-overhead comparisons')
    parser.add_argument('--fixture', action='store_true', help='Require the private ThroughputFixture build and measure scheduled safety events')
    parser.add_argument('--checkpoint', type=Path, help='Copy an unchanged older native checkpoint into each private profile; skips fresh starting-supply assertions')
    parser.add_argument('--runtime-seconds', type=int, default=0, help='Instead measure the production headless loop with the read-only dashboard sampler')
    parser.add_argument('--runtime-accelerated', action='store_true', help='Enable bounded test acceleration in the production-loop measurement')
    parser.add_argument('--runtime-unbatched-observations', action='store_true', help='Compare the legacy seven-call observation path')
    parser.add_argument('--profile-controller', action='store_true', help='Retain Python CPU and nested controller wall timings')
    parser.add_argument('--worker-storage', choices=['volume', 'bind'], default='volume',
                        help='Durable Linux worker state, or host bind for an explicit storage comparison')
    parser.add_argument('--observation-comparison', action='store_true', help='Verify paired paused native observations before the runtime sample')
    parser.add_argument('--placement-comparison', action='store_true', help='Verify bounded placement batches against individual native previews')
    parser.add_argument('--search-comparison', action='store_true', help='Verify native site search and material alternative equivalence')
    parser.add_argument('--shell-comparison', action='store_true', help='Compare read-only Hands shell preflight with shared native reads')
    parser.add_argument('--modes', nargs='+', choices=['headless', 'rendered', 'suspended', 'capture'], default=['headless'])
    parser.add_argument('--repeats', type=int, default=2)
    parser.add_argument('--ticks', type=int, nargs='+', default=[37, 600, 6000])
    parser.add_argument('--timeout', type=int, default=1800)
    args = parser.parse_args()
    if not 1 <= args.repeats <= 100 or any(not 1 <= ticks <= 1800000 for ticks in args.ticks):
        parser.error('Use 1..100 repeats and 1..1800000 ticks')
    if len(args.modes) != len(set(args.modes)):
        parser.error('Each render mode must appear once')
    if args.runtime_seconds and (not 10 <= args.runtime_seconds <= 1800 or args.modes != ['headless']):
        parser.error('Runtime measurement needs 10..1800 seconds and headless mode')
    if args.runtime_accelerated and not args.runtime_seconds:
        parser.error('--runtime-accelerated requires --runtime-seconds')
    raise SystemExit(0 if run(args) else 1)

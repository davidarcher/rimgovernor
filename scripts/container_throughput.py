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
    cache = None if args.no_input_cache else prepare_cache(args.game, args.mods, args.gabs/'gabs', image, output)
    report = dict(passed=False, image=image, workers=[], input_cache=cache, start_type='fresh_baseline')
    profile = args.profile.resolve()
    if args.checkpoint:
        profile = output/'profile'
        (profile/'Config').mkdir(parents=True)
        (profile/'Saves').mkdir()
        for filename in ('Prefs.xml', 'ModsConfig.xml'):
            shutil.copy2(args.profile/'Config'/filename, profile/'Config'/filename)
        shutil.copy2(args.checkpoint, profile/'Saves/RimBot-tribal8-baseline.rws')
        report.update(start_type='saved_checkpoint', checkpoint=str(args.checkpoint.resolve()),
                      checkpoint_sha256=hashlib.sha256(args.checkpoint.read_bytes()).hexdigest())
    for mode in args.modes:
        root = output/mode
        root.mkdir()
        name = 'rimbot-b16-'+uuid.uuid4().hex[:12]
        peers = command('ps', '--format', '{{json .}}', capture_output=True, text=True, check=True).stdout
        worker = dict(mode=mode, container=name, other_containers=[json.loads(line) for line in peers.splitlines()], passed=False)
        worker['resource_state_before'] = command('stats', '--no-stream', '--format', '{{json .}}',
            capture_output=True, text=True, timeout=30, check=True).stdout
        report['workers'].append(worker)
        invocation = ['run', '--rm', '--init', '--name', name]
        if cache:
            invocation.extend(['--mount', f"type=volume,source={cache['volume']},target=/cached-inputs,readonly",
                               '-e', 'RIMBOT_INPUT_CACHE_ROOT=/cached-inputs/snapshot',
                               '-e', 'RIMBOT_INPUT_CACHE_KEY='+cache['key']])
        for path, destination in ((args.game, 'game'), (args.mods, 'mods'), (profile, 'profile'), (args.gabs, 'gabs')):
            invocation.extend(['--mount', f'type=bind,source={path.resolve()},target=/inputs/{destination},readonly'])
        invocation.extend(['--mount', f'type=bind,source={root},target=/worker', image,
                           '--unity-gc-time-slice', '0', '--display', 'headless' if mode == 'headless' else 'xvfb',
                           '--', 'python', 'scripts/throughput_acceptance.py', '--mode', mode,
                           '--repeats', str(args.repeats), '--ticks', *map(str, args.ticks)])
        if args.fixture:
            invocation.append('--fixture')
        if args.checkpoint:
            invocation.append('--saved-checkpoint')
        worker['command'] = invocation
        started = time.perf_counter()
        try:
            with (root/'container.log').open('w', encoding='utf8') as stream:
                result = command(*invocation, stdout=stream, stderr=subprocess.STDOUT, timeout=args.timeout)
            worker['exit_code'] = result.returncode
            evidence = root/'run/throughput-result.json'
            worker['passed'] = result.returncode == 0 and evidence.is_file() and json.loads(evidence.read_text()).get('passed') is True
        except Exception as error:
            worker['error'] = repr(error)
        finally:
            cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
            (root/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr, encoding='utf8')
            worker['cleanup_ok'] = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
            worker['passed'] = worker['passed'] and worker['cleanup_ok']
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
    parser.add_argument('--fixture', action='store_true', help='Require the private ThroughputFixture build and measure scheduled safety events')
    parser.add_argument('--checkpoint', type=Path, help='Copy an unchanged older native checkpoint into each private profile; skips fresh starting-supply assertions')
    parser.add_argument('--modes', nargs='+', choices=['headless', 'rendered', 'suspended'], default=['headless'])
    parser.add_argument('--repeats', type=int, default=2)
    parser.add_argument('--ticks', type=int, nargs='+', default=[37, 600, 6000])
    parser.add_argument('--timeout', type=int, default=1800)
    args = parser.parse_args()
    if not 1 <= args.repeats <= 100 or any(not 1 <= ticks <= 1800000 for ticks in args.ticks):
        parser.error('Use 1..100 repeats and 1..1800000 ticks')
    if len(args.modes) != len(set(args.modes)):
        parser.error('Each render mode must appear once')
    raise SystemExit(0 if run(args) else 1)

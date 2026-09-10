"""Run the world progression probe in one disposable local Linux Docker game."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid

from container_checks import docker_environment


def run(args):
    source = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    docker, environment = docker_environment()
    def command(*values, **kwargs):
        return subprocess.run([docker, *values], cwd=source, env=environment, **kwargs)
    name = 'rimbot-b15-' + uuid.uuid4().hex[:12]
    report = dict(passed=False, container=name, trip=args.trip, shared=args.shared, quests=args.quests, days=args.days, matrix=args.matrix,
                  scope='Native caravan/quest observations and optional ordinary loaded caravan round trip')
    try:
        if not args.no_build:
            with (output / 'build.log').open('w') as log:
                command('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                        stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
        image = command('image', 'inspect', '--format', '{{.Id}}', args.image,
                        capture_output=True, text=True, check=True).stdout.strip()
        report['image'] = image
        # Preserve the exact probe independently of image cache or later worktree edits.
        probe = (source / 'scripts/world_progression_acceptance.py').read_bytes()
        (output / 'probe.py').write_bytes(probe)
        report['probe_sha256'] = hashlib.sha256(probe).hexdigest()
        mounts = []
        for path, target in [(args.game, 'game'), (args.mods, 'mods'), (args.profile, 'profile'), (args.gabs, 'gabs')]:
            path = path.resolve()
            if not path.is_dir():
                raise ValueError(f'Missing native input directory: {path}')
            mounts += ['--mount', f'type=bind,source={path},target=/inputs/{target},readonly']
        mounts += ['--mount', f'type=bind,source={output},target=/worker']
        with (output / 'container.log').open('w') as log:
            result = command('run', '--name', name, '--init', '--env', 'RIMBOT_UNITY_GC_TIME_SLICE=0',
                '--env', 'PYTHONPATH=/app/scripts:/app/controller',
                '--env', 'RIMBOT_SHARED_WORLD=' + ('1' if args.shared else '0'),
                '--env', 'RIMBOT_QUEST_PROBE=' + ('1' if args.quests else '0'),
                '--env', 'RIMBOT_SURVIVAL_DAYS=' + str(args.days),
                '--env', 'RIMBOT_WORLD_MATRIX=' + ('1' if args.matrix else '0'),
                '--env', 'RIMBOT_CARAVAN_TRIP=' + ('1' if args.trip else '0'), *mounts,
                image, '--', 'python', '/worker/probe.py', stdout=log, stderr=subprocess.STDOUT,
                timeout=args.timeout)
        report['exit_code'] = result.returncode
        native = output / 'world-progression/result.json'
        if native.is_file():
            report['native_passed'] = json.loads(native.read_text())['passed']
        report['passed'] = result.returncode == 0 and report.get('native_passed') is True
    except Exception as error:
        report['error'] = repr(error)
    finally:
        try:
            cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=60)
            (output / 'cleanup.log').write_text(cleanup.stdout + cleanup.stderr)
            report['cleanup_ok'] = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
        except Exception as error:
            report['cleanup_ok'] = False
            report['cleanup_error'] = repr(error)
        report['passed'] = report['passed'] and report['cleanup_ok']
        (output / 'result.json').write_text(json.dumps(report, indent=2))
        print(json.dumps(report, indent=2), flush=True)
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--image', default='rimbot-b15:local')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--trip', action='store_true')
    parser.add_argument('--shared', action='store_true', help='Use shared semantic commands and Hands for the trip')
    parser.add_argument('--quests', action='store_true', help='Require ordinary join-quest outcome using the separate incident fixture')
    parser.add_argument('--days', type=int, choices=range(0, 61), default=0, help='Additional ordinary survival days with living roster checks')
    parser.add_argument('--timeout', type=int, default=2400)
    parser.add_argument('--matrix', action='store_true', help='Native reserve competition, cold-readiness refusal and emergency clock refusal')
    args = parser.parse_args()
    if args.matrix and not (args.trip and args.shared):
        parser.error('--matrix requires --trip --shared')
    raise SystemExit(0 if run(args) else 1)

"""Run the husbandry pawn-outcome probe in one isolated local Docker worker."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

from container_checks import docker_environment


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    docker, environment = docker_environment()
    source = Path(__file__).resolve().parents[1]

    def command(*values, **kwargs):
        return subprocess.run([docker, *values], cwd=source, env=environment, **kwargs)

    if not args.no_build:
        with (output/'build.log').open('w', encoding='utf8') as stream:
            command('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                    stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=1800)
    image = command('image', 'inspect', '--format', '{{.Id}}', args.image,
                    capture_output=True, text=True, check=True).stdout.strip()
    name = 'rimbot-husbandry-' + uuid.uuid4().hex[:12]
    invocation = ['run', '--rm', '--init', '--name', name]
    for path, destination in ((args.game, 'game'), (args.mods, 'mods'),
                              (args.profile, 'profile'), (args.gabs, 'gabs')):
        invocation.extend(['--mount', f'type=bind,source={path.resolve()},target=/inputs/{destination},readonly'])
    invocation.extend(['--mount', f'type=bind,source={output},target=/worker', image,
                       '--unity-gc-time-slice', '0', '--', 'python', 'scripts/husbandry_acceptance.py'])
    report = {'passed': False, 'image': image, 'container': name, 'command': [docker, *invocation],
              'scope': 'Seeded local native husbandry outcomes; no inference or sustained seasonal survival.'}
    try:
        with (output/'container.log').open('w', encoding='utf8') as stream:
            result = command(*invocation, stdout=stream, stderr=subprocess.STDOUT, timeout=args.timeout)
        report['exit_code'] = result.returncode
        native_path = output/'run/husbandry-result.json'
        report['native_report'] = str(native_path)
        report['passed'] = result.returncode == 0 and native_path.is_file() and json.loads(
            native_path.read_text(encoding='utf8')).get('passed') is True
    except Exception as error:
        report['error'] = repr(error)
    finally:
        try:
            cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
            (output/'cleanup.log').write_text(cleanup.stdout + cleanup.stderr, encoding='utf8')
            report['cleanup_ok'] = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
        except Exception as error:
            report['cleanup_ok'], report['cleanup_error'] = False, repr(error)
        report['passed'] = report['passed'] and report['cleanup_ok']
        (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report, indent=2), flush=True)
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--' + argument, type=Path, required=True)
    parser.add_argument('--image', default='rimbot-worker:husbandry')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--timeout', type=int, default=1200)
    raise SystemExit(0 if run(parser.parse_args()) else 1)

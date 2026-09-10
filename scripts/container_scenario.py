"""Standard native scenario launcher: isolated inputs, automatic dashboard port, owned cleanup."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

from container_checks import docker_environment


def dashboard_options(name, display='headless'):
    return ['--publish', '127.0.0.1::8787', '--label', 'io.rimbot.colony=1',
            '--label', 'io.rimbot.colony.name='+name, '-e', 'RIMBOT_DISPLAY='+display]


def require_dashboard_image(call, image):
    version = call('image', 'inspect', '--format', '{{ index .Config.Labels "io.rimbot.scenario-dashboard" }}',
                   image, capture_output=True, text=True, check=True).stdout.strip()
    if version != '1':
        raise ValueError('Rebuild the worker image: it lacks the scenario dashboard hook')


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parents[1]
    docker, environment = docker_environment()
    def call(*values, **kwargs):
        return subprocess.run([docker, *values], cwd=source, env=environment, **kwargs)
    name = 'rimbot-scenario-'+uuid.uuid4().hex[:12]
    report = {'container': name, 'passed': False, 'scope': 'Scenario exit status; inspect its native assertions separately.'}
    try:
        if not args.no_build:
            with (output/'build.log').open('w', encoding='utf8') as log:
                call('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                     stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
        image = call('image', 'inspect', '--format', '{{.Id}}', args.image,
                     capture_output=True, text=True, check=True).stdout.strip()
        report['image'] = image
        require_dashboard_image(call, image)
        mounts = []
        for key in ('game', 'mods', 'profile', 'gabs'):
            mounts += ['--mount', f'type=bind,source={getattr(args, key).resolve()},target=/inputs/{key},readonly']
        mounts += ['--mount', f'type=bind,source={output},target=/worker']
        command = args.command
        if command and command[0] == '--':
            command = command[1:]
        if not command:
            raise ValueError('Provide a scenario command after --')
        call('run', '-d', '--init', '--name', name, *dashboard_options(args.name or name, args.display),
             *mounts, image, '--display', args.display, '--unity-gc-time-slice', '0', '--', *command,
             capture_output=True, text=True, check=True, timeout=120)
        address = call('port', name, '8787/tcp', capture_output=True, text=True, check=True).stdout.strip()
        report['dashboard_url'] = 'http://'+address+'/scenario'
        (output/'dashboard.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        print('Scenario dashboard: '+report['dashboard_url'], flush=True)
        result = call('wait', name, capture_output=True, text=True, check=True, timeout=args.timeout)
        report['exit_code'] = int(result.stdout.strip())
        report['passed'] = report['exit_code'] == 0
    except Exception as error:
        report['error'] = repr(error)
    finally:
        try:
            with (output/'container.log').open('w', encoding='utf8') as log:
                call('logs', name, stdout=log, stderr=subprocess.STDOUT, timeout=30)
        except Exception as error:
            report['log_error'] = repr(error)
        try:
            cleanup = call('rm', '-f', name, capture_output=True, text=True, timeout=30)
            report['cleanup_ok'] = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
            (output/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr, encoding='utf8')
        except Exception as error:
            report['cleanup_ok'], report['cleanup_error'] = False, repr(error)
        report['passed'] = report['passed'] and report['cleanup_ok']
        (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report, indent=2), flush=True)
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--'+key, type=Path, required=True)
    parser.add_argument('--image', required=True, help='Task-specific worker image tag')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--name', help='Friendly name shown in the colony directory')
    parser.add_argument('--display', choices=['headless', 'xvfb'], default='headless')
    parser.add_argument('--timeout', type=int, default=3600)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    raise SystemExit(0 if run(parser.parse_args()) else 1)

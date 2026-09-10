"""Run focused or complete controller suites in independent Docker containers and retain evidence."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import uuid


def docker_environment():
    executable = shutil.which('docker')
    if executable is None and os.name == 'nt':
        for directory in (Path(os.environ['LOCALAPPDATA'])/'Programs/DockerDesktop/resources/bin',
                          Path(os.environ['ProgramFiles'])/'Docker/Docker/resources/bin'):
            if (directory/'docker.exe').is_file():
                executable = str(directory/'docker.exe')
                break
    if executable is None:
        raise ValueError('Install/start Docker Desktop (Linux containers) or Docker Engine first')
    environment = dict(os.environ)
    environment['PATH'] = str(Path(executable).parent)+os.pathsep+environment.get('PATH', '')
    return executable, environment


def run(output, workers=1, image='rimbot-checks:local', build=True, timeout=600,
        tests=(), keyword=None, controller_only=False):
    if not 1 <= workers <= 8:
        raise ValueError('workers must be between 1 and 8')
    tests = list(tests)
    if any(not test.startswith('controller_tests/') or '..' in test.split('/') for test in tests):
        raise ValueError('tests must be repository-relative controller_tests/ paths or node IDs')
    target = 'controller-tests' if controller_only else 'tests'
    selection = tests + (['-k', keyword] if keyword else [])
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    docker, environment = docker_environment()
    source = Path(__file__).resolve().parents[1]
    def command(*args, **kwargs):
        return subprocess.run([docker, *args], env=environment, cwd=source, **kwargs)
    if build:
        with (output/'build.log').open('w', encoding='utf8') as log:
            result = command('build', '-f', 'containers/Dockerfile', '--target', target,
                             '-t', image, '.', stdout=log, stderr=subprocess.STDOUT, timeout=1800)
        if result.returncode:
            raise RuntimeError(f'Image build failed; see {output / "build.log"}')
    image_id = command('image', 'inspect', '--format', '{{.Id}}', image,
                       check=True, capture_output=True, text=True).stdout.strip()
    prefix = 'rimbot-check-'+uuid.uuid4().hex[:12]
    began = time.monotonic()
    def worker(index):
        destination = output/str(index)
        destination.mkdir()
        name = f'{prefix}-{index}'
        start = time.monotonic()
        error = None
        code = None
        cleanup_code = None
        cleanup_ok = False
        try:
            with (destination/'pytest.log').open('w', encoding='utf8') as log:
                result = command('run', '--rm', '--init', '--name', name,
                    '--mount', f'type=bind,source={destination},target=/artifacts',
                    '--entrypoint', 'python', image_id, '-m', 'pytest', '-q', '--basetemp=/tmp/rimbot-pytest',
                    '--junitxml=/artifacts/junit.xml', '--durations=10', *selection,
                    stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
            code = result.returncode
        except Exception as exc:
            error = str(exc)
        finally:
            # Remove only this invocation's named container after timeout/failure.
            try:
                cleanup = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
                cleanup_code = cleanup.returncode
                cleanup_ok = cleanup.returncode == 0 or 'No such container' in cleanup.stderr
                (destination/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr, encoding='utf8')
            except Exception as exc:
                (destination/'cleanup.log').write_text(str(exc), encoding='utf8')
        return dict(worker=index, container=name, exit_code=code, error=error,
                    elapsed_seconds=round(time.monotonic()-start, 3),
                    junit_present=(destination/'junit.xml').is_file(), cleanup_exit_code=cleanup_code, cleanup_ok=cleanup_ok)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(worker, range(workers)))
    report = dict(image=image_id, workers=results, elapsed_seconds=round(time.monotonic()-began, 3),
                  passed=all(row['exit_code'] == 0 and row['junit_present'] and row['cleanup_ok'] for row in results),
                  tests=tests, keyword=keyword, build_target=target, image_built=build,
                  dashboard_checked=build and not controller_only,
                  scope='Linux controller fixtures; each worker repeats the selection. No native RimWorld or model inference.')
    (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report, indent=2), flush=True)
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--workers', type=int, choices=range(1, 9), default=1,
                        help='Independent repetitions of the selection, not shards (default: 1)')
    parser.add_argument('--image', default='rimbot-checks:local')
    parser.add_argument('--no-build', action='store_true', help='Use the specified existing image')
    parser.add_argument('--timeout', type=int, default=600, help='Per-container wall-time limit in seconds')
    parser.add_argument('--test', action='append', default=[],
                        help='Repository-relative controller_tests/ path or node ID; repeat to select more')
    parser.add_argument('-k', '--keyword', help='Pytest keyword expression')
    parser.add_argument('--controller-only', action='store_true',
                        help='Build controller fixtures without dashboard checks/assets')
    args = parser.parse_args()
    raise SystemExit(0 if run(args.output, args.workers, args.image, not args.no_build, args.timeout,
                             args.test, args.keyword, args.controller_only)['passed'] else 1)

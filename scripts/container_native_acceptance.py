"""Accept two real Linux game containers through the normal controller API."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import hashlib
from pathlib import Path
import subprocess
import struct
import time
import urllib.error
import urllib.request
import uuid

from container_checks import docker_environment
from container_camera_acceptance import accept_camera
from container_input_cache import prepare_cache, compose_override


def run(args):
    if args.camera_acceptance and args.display != 'xvfb':
        raise ValueError('--camera-acceptance requires --display xvfb')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parents[1]
    docker, environment = docker_environment()
    def docker_run(*command, **kwargs):
        return subprocess.run([docker, *command], env=environment, cwd=source, **kwargs)
    if not args.no_build:
        with (output/'build.log').open('w', encoding='utf8') as stream:
            docker_run('build', '-f', 'containers/Dockerfile', '--target', 'worker',
                       '-t', args.image, '.', stdout=stream, stderr=subprocess.STDOUT,
                       check=True, timeout=1800)
    image = docker_run('image', 'inspect', '--format', '{{.Id}}', args.image,
                       capture_output=True, text=True, check=True).stdout.strip()
    override = output/'image.yaml'
    cache = None if args.no_input_cache else prepare_cache(args.game, args.mods, args.gabs/'gabs', image, output)
    override.write_text(json.dumps(compose_override(image, cache), indent=2), encoding='utf8')
    prefix = 'rimbot-native-'+uuid.uuid4().hex[:10]
    workers = []
    for index in range(2):
        root = output/str(index)
        root.mkdir()
        env = dict(environment, RIMBOT_LINUX_GAME=str(args.game.resolve()),
                   RIMBOT_WORKER_MODS=str(args.mods.resolve()), RIMBOT_WORKER_PROFILE=str(args.profile.resolve()),
                   RIMBOT_LINUX_GABS=str(args.gabs.resolve()), RIMBOT_WORKER_OUTPUT=str(root), RIMBOT_WORKER_PORT='0',
                   RIMBOT_DISPLAY=args.display, RIMBOT_DISPLAY_RESOLUTION=args.resolution)
        workers.append(dict(root=root, project=f'{prefix}-{index}', env=env))
    def compose(worker, *command, **kwargs):
        return subprocess.run([docker, 'compose', '-f', str(source/'containers/compose.yaml'),
                               '-f', str(override), '-p', worker['project'], *command],
                              env=worker['env'], cwd=source, **kwargs)
    def api(worker, endpoint, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(worker['url']+endpoint, data=data,
            headers={'Content-Type': 'application/json', 'x-rimbot': '1'})
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.load(response)
    def start(worker):
        with (worker['root']/'compose.log').open('w', encoding='utf8') as stream:
            compose(worker, 'up', '--no-build', '-d', stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=120)
        address = compose(worker, 'port', 'worker', '8787', capture_output=True, text=True, check=True).stdout.strip()
        worker['url'] = 'http://'+address
        deadline = time.monotonic()+args.startup_timeout
        last = None
        while time.monotonic() < deadline:
            running = compose(worker, 'ps', '--status', 'running', '-q', 'worker',
                              capture_output=True, text=True, check=True, timeout=15).stdout.strip()
            if not running:
                raise RuntimeError('Worker exited during startup; inspect container.log')
            try:
                last = api(worker, '/api/state')
                if last.get('connected'):
                    worker['session'] = last['sessionId']
                    return last
                if last.get('status', {}).get('label') == 'Connection failed':
                    raise RuntimeError('Native bridge failed: '+json.dumps(last.get('feed', [])[-2:]))
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                pass
            time.sleep(1)
        raise TimeoutError('Native startup timed out: '+json.dumps(last))
    def capture(worker, name, previous=None, keepalive=None):
        baseline_version = api(worker, '/api/state')['cameraVersion']
        deadline = time.monotonic()+60
        while time.monotonic() < deadline:
            if keepalive:
                keepalive()
            api(worker, '/api/video', {'viewer': 'container-acceptance', 'playing': True})
            state = api(worker, '/api/state')
            assert state['sessionId'] == worker['session'], 'Colony changed during capture'
            if state['cameraVersion'] <= baseline_version:
                time.sleep(1)
                continue
            try:
                with urllib.request.urlopen(worker['url']+'/api/camera', timeout=10) as response:
                    frame = response.read()
                assert frame.startswith(b'\x89PNG\r\n\x1a\n'), 'Expected PNG frame'
                dimensions = struct.unpack('>II', frame[16:24])
                assert dimensions == tuple(map(int, args.resolution.split('x'))), dimensions
                assert len(frame) > 10000, 'Empty rendered frame'
                digest = hashlib.sha256(frame).hexdigest()
                if digest == previous:
                    time.sleep(1)
                    continue
                (worker['root']/name).write_bytes(frame)
                return dict(dimensions=dimensions, bytes=len(frame), sha256=digest,
                            after_camera_version=baseline_version, observed_camera_version=state['cameraVersion'])
            except urllib.error.HTTPError as error:
                if error.code != 503:
                    raise
            time.sleep(1)
        raise TimeoutError('No native rendered frame')

    def clock(worker, speed):
        return api(worker, '/api/time', {'session_id': worker['session'], 'speed': speed})['game']
    def stopped_state(worker):
        identity = compose(worker, 'ps', '-a', '-q', 'worker', capture_output=True, text=True, check=True).stdout.strip()
        state = json.loads(docker_run('inspect', '--format', '{{json .State}}', identity,
                           capture_output=True, text=True, check=True).stdout)
        result = {key: state[key] for key in ('Status', 'Running', 'ExitCode', 'OOMKilled', 'Error')}
        logs = compose(worker, 'logs', '--no-color', capture_output=True, text=True, check=True)
        result['controller_shutdown_complete'] = 'Application shutdown complete.' in logs.stdout+logs.stderr
        result['clean'] = (not state['Running'] and not state['OOMKilled'] and not state['Error']
                           and state['ExitCode'] in (0, 143) and result['controller_shutdown_complete'])
        return result
    report = dict(image=image, input_cache=cache, display=args.display, resolution=args.resolution, passed=False, scope='Native Linux discovery, independent clocks, peer survival and retained checkpoint; no pawn-work or sustained throughput acceptance.')
    began = time.monotonic()
    try:
        with ThreadPoolExecutor(max_workers=2) as pool:
            report['initial_states'] = list(pool.map(start, workers))
        before = [clock(worker, 'Paused') for worker in workers]
        report['before'] = before
        if args.display == 'xvfb':
            report['frames'] = [capture(worker, 'frame.png') for worker in workers]
        if args.camera_acceptance:
            report['camera_controls'] = {}
            accept_camera(lambda endpoint, body=None: api(workers[0], endpoint, body),
                          lambda name, previous=None, keepalive=None: capture(workers[0], name, previous, keepalive),
                          workers[0]['session'], before[0]['ticksGame'], report['camera_controls'])
        clock(workers[0], 'Normal')
        deadline = time.monotonic()+30
        while time.monotonic() < deadline:
            state = api(workers[0], '/api/state')
            if state['game']['tick'] > before[0]['ticksGame']:
                break
            time.sleep(1)
        after = [clock(worker, 'Paused') for worker in workers]
        report['after'] = after
        assert after[0]['ticksGame'] > before[0]['ticksGame'], after
        assert after[1]['ticksGame'] == before[1]['ticksGame'], after
        report['independent_clocks'] = True
        stopped = compose(workers[0], 'stop', capture_output=True, text=True, check=True, timeout=90)
        (workers[0]['root']/'stop.log').write_text(stopped.stdout+stopped.stderr, encoding='utf8')
        report['first_shutdown'] = stopped_state(workers[0])
        assert report['first_shutdown']['clean'], report['first_shutdown']
        peer = clock(workers[1], 'Paused')  # Native readback, not merely cached HTTP state.
        assert peer['ticksGame'] == before[1]['ticksGame'], peer
        report['peer_after_stop'] = peer
        report['peer_survival'] = True
        if args.display == 'xvfb':
            report['survivor_camera'] = api(workers[1], '/api/camera/navigate',
                {'session_id': workers[1]['session'], 'action': 'right'})
            report['survivor_frame'] = capture(workers[1], 'survivor.png', report['frames'][1]['sha256'])
            assert clock(workers[1], 'Paused')['ticksGame'] == before[1]['ticksGame']
        if args.player_input:
            from player_input_acceptance import accept
            report['player_input'] = accept(api, workers[1])
        checkpoint = api(workers[1], '/api/session/checkpoint', {'session_id': workers[1]['session']})
        report['checkpoint'] = checkpoint
        manifest = Path(checkpoint['manifest_path']).relative_to('/worker')
        retained = workers[1]['root']/manifest
        assert retained.is_file(), retained
        report['checkpoint_retained'] = str(retained)
        report['passed'] = True
    except Exception as error:
        report['error'] = repr(error)
        if isinstance(error, urllib.error.HTTPError):
            report['http_error_detail'] = error.read().decode('utf8', errors='replace')
    finally:
        report['cleanup'] = []
        for worker in workers:
            cleanup = dict(project=worker['project'])
            try:
                stopped = compose(worker, 'stop', capture_output=True, text=True, timeout=90)
                cleanup['stop_exit_code'] = stopped.returncode
                cleanup['state'] = stopped_state(worker)
                if stopped.returncode or not cleanup['state']['clean']:
                    report['passed'] = False
                with (worker['root']/'container.log').open('w', encoding='utf8') as stream:
                    compose(worker, 'logs', '--no-color', stdout=stream, stderr=subprocess.STDOUT, timeout=30)
            except Exception as error:
                cleanup['error'] = repr(error)
                report['passed'] = False
            finally:
                try:
                    removed = compose(worker, 'down', capture_output=True, text=True, timeout=90)
                    (worker['root']/'cleanup.log').write_text(removed.stdout+removed.stderr, encoding='utf8')
                    cleanup['exit_code'] = removed.returncode
                    if removed.returncode:
                        report['passed'] = False
                except Exception as error:
                    cleanup['removal_error'] = repr(error)
                    report['passed'] = False
                report['cleanup'].append(cleanup)
        if report.get('checkpoint_retained'):
            try:
                retained = Path(report['checkpoint_retained'])
                manifest = json.loads(retained.read_text(encoding='utf8'))
                for name, key in (('game.rws', 'game_sha256'), ('bridge.sqlite', 'database_sha256')):
                    with (retained.parent/name).open('rb') as stream:
                        assert hashlib.file_digest(stream, 'sha256').hexdigest() == manifest[key], name
                report['checkpoint_hashes_after_cleanup'] = True
            except Exception as error:
                report['checkpoint_retention_error'] = repr(error)
                report['passed'] = False
        report['elapsed_seconds'] = round(time.monotonic()-began, 3)
        (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    summary = {key: value for key, value in report.items() if key not in ('initial_states', 'camera_controls')}
    if 'camera_controls' in report:
        controls = report['camera_controls']
        summary['camera_controls'] = dict(passed=controls['passed'],
            operations=len(controls['operations']), refusals=len(controls['refusals']))
    print(json.dumps(summary, indent=2))
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('game', 'mods', 'profile', 'gabs', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--camera-acceptance', action='store_true', help='Verify native camera controls and viewer lease guards')
    parser.add_argument('--display', choices=['headless', 'xvfb'], default='headless')
    parser.add_argument('--player-input', action='store_true', help='Verify B18 handoff and selection in a rendered worker')
    parser.add_argument('--resolution', default='1280x720')
    parser.add_argument('--image', default='rimbot-worker:local')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--no-input-cache', action='store_true', help='Copy inputs directly from bind mounts for an uncached comparison')
    parser.add_argument('--startup-timeout', type=int, default=240)
    raise SystemExit(0 if run(parser.parse_args()) else 1)

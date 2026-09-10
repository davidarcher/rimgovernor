"""Run the world progression probe in one disposable local Linux Docker game."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid

from container_checks import docker_environment
from container_scenario import dashboard_options, require_dashboard_image


def run(args):
    source = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    docker, environment = docker_environment()
    def command(*values, **kwargs):
        return subprocess.run([docker, *values], cwd=source, env=environment, **kwargs)
    name = 'rimgovernor-b15-' + uuid.uuid4().hex[:12]
    report = dict(passed=False, container=name, trip=args.trip, shared=args.shared, quests=args.quests, days=args.days, matrix=args.matrix,
                  logistics=args.logistics, multimap=args.multimap, emergency=args.emergency, diplomacy=args.diplomacy, quest_trade=args.quest_trade, expired=args.expired, failed=args.failed,
                  prepared_days=args.prepared_days, recovery=args.recovery, resume_trip=args.resume_trip,
                  scope='Native caravan/quest observations and optional ordinary loaded caravan round trip')
    try:
        baseline = args.profile.resolve() / 'Saves/RimGovernor-tribal8-baseline.rws'
        if baseline.is_file():
            report['baseline'] = dict(path=str(baseline), sha256=hashlib.sha256(baseline.read_bytes()).hexdigest())
        required = ['RimGovernor.Observations.BridgeTools.dll', 'RimGovernor.ColonyIdentity.dll', 'HeadlessRimPatch.dll']
        if args.quests or args.matrix or args.emergency or args.multimap or args.quest_trade or args.expired or args.failed:
            required.append('RimGovernor.InterruptionFixtures.BridgeTools.dll')
        report['assemblies'] = {}
        for assembly in required:
            matches = list(args.mods.resolve().rglob(assembly))
            if len(matches) != 1:
                raise ValueError(f'Require exactly one {assembly}; found {len(matches)} in the private mod snapshot')
            report['assemblies'][assembly] = dict(path=str(matches[0]),
                sha256=hashlib.sha256(matches[0].read_bytes()).hexdigest())
        if not args.no_build:
            with (output / 'build.log').open('w') as log:
                command('build', '-f', 'containers/Dockerfile', '--target', 'worker', '-t', args.image, '.',
                        stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
        image = command('image', 'inspect', '--format', '{{.Id}}', args.image,
                        capture_output=True, text=True, check=True).stdout.strip()
        report['image'] = image
        require_dashboard_image(command, image)
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
        command('run', '-d', '--name', name, '--init', *dashboard_options(name), '--env', 'RIMGOVERNOR_UNITY_GC_TIME_SLICE=0',
                '--env', 'PYTHONPATH=/app/scripts:/app/controller',
                '--env', 'RIMGOVERNOR_RESUME_WORLD=' + ('1' if args.resume_trip else '0'),
                '--env', 'RIMGOVERNOR_RECOVERY=' + ('1' if args.recovery else '0'),
                '--env', 'RIMGOVERNOR_SHARED_WORLD=' + ('1' if args.shared else '0'),
                '--env', 'RIMGOVERNOR_QUEST_PROBE=' + ('1' if args.quests else '0'),
                '--env', 'RIMGOVERNOR_SURVIVAL_DAYS=' + str(args.days),
                '--env', 'RIMGOVERNOR_PREPARED_DAYS=' + ('1' if args.prepared_days else '0'),
                '--env', 'RIMGOVERNOR_WORLD_MATRIX=' + ('1' if args.matrix else '0'),
                '--env', 'RIMGOVERNOR_EMERGENCY_PROBE=' + ('1' if args.emergency else '0'),
                '--env', 'RIMGOVERNOR_LOGISTICS=' + ('1' if args.logistics else '0'),
                '--env', 'RIMGOVERNOR_MULTIMAP=' + ('1' if args.multimap else '0'),
                '--env', 'RIMGOVERNOR_DIPLOMACY=' + ('1' if args.diplomacy else '0'),
                '--env', 'RIMGOVERNOR_QUEST_TRADE=' + ('1' if args.quest_trade else '0'),
                '--env', 'RIMGOVERNOR_FAILED_QUEST=' + ('1' if args.failed else '0'),
                '--env', 'RIMGOVERNOR_EXPIRED_QUEST=' + ('1' if args.expired else '0'),
                '--env', 'RIMGOVERNOR_CARAVAN_TRIP=' + ('1' if args.trip else '0'), *mounts,
                image, '--', 'python', '/worker/probe.py', capture_output=True, text=True, check=True, timeout=120)
        address = command('port', name, '8787/tcp', capture_output=True, text=True, check=True).stdout.strip()
        report['dashboard_url'] = 'http://' + address + '/scenario'
        (output / 'dashboard.json').write_text(json.dumps(report, indent=2))
        print('Scenario dashboard: ' + report['dashboard_url'], flush=True)
        result = command('wait', name, capture_output=True, text=True, check=True, timeout=args.timeout)
        report['exit_code'] = int(result.stdout.strip())
        native = output / 'world-progression/result.json'
        if native.is_file():
            report['native_passed'] = json.loads(native.read_text())['passed']
        report['passed'] = report['exit_code'] == 0 and report.get('native_passed') is True
    except Exception as error:
        report['error'] = repr(error)
    finally:
        try:
            with (output / 'container.log').open('w') as log:
                command('logs', name, stdout=log, stderr=subprocess.STDOUT, timeout=30)
        except Exception as error:
            report['log_error'] = repr(error)
        try:
            location = output / 'world-progression/runtime-location.json'
            retained = output / 'world-progression/native-runtime'
            if location.is_file() and not retained.exists():
                runtime = json.loads(location.read_text())['root']
                if not (runtime.startswith('/tmp/rimgovernor-world-') and runtime.endswith('/run') and '..' not in runtime):
                    raise ValueError('Unexpected private runtime path')
                copied = command('cp', name + ':' + runtime, str(retained), capture_output=True, text=True, timeout=120)
                report['runtime_recovery_ok'] = copied.returncode == 0
                (output / 'runtime-recovery.log').write_text(copied.stdout + copied.stderr)
        except Exception as error:
            report['runtime_recovery_error'] = repr(error)
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
    parser.add_argument('--image', default='rimgovernor-b15:local')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--trip', action='store_true')
    parser.add_argument('--resume-trip', action='store_true', help='Continue one observed checkpoint caravan toward its ongoing trade quest, then verify rewards and return storage')
    parser.add_argument('--recovery', action='store_true', help='Observe ordinary ration depletion and explicit living return of a short-supplied party')
    parser.add_argument('--logistics', action='store_true', help='Verify explicit hold and return cargo unloading into native storage')
    parser.add_argument('--diplomacy', action='store_true', help='Visit a native settlement and give explicitly requested silver through shared Hands')
    parser.add_argument('--quest-trade', action='store_true', help='Acquire ordinary requested goods, visit the quest settlement and observe native fulfillment and rewards')
    parser.add_argument('--failed', action='store_true', help='Reject an ordinary auto-accepted join quest through its native choice and observe failure without admission')
    parser.add_argument('--expired', action='store_true', help='Wait for an ordinary short-lived unaccepted quest to expire and verify it cannot be accepted')
    parser.add_argument('--multimap', action='store_true', help='Settle a second native map and verify scope invalidation; requires private profile allowing two settlements')
    parser.add_argument('--shared', action='store_true', help='Use shared semantic commands and Hands for the trip')
    parser.add_argument('--quests', action='store_true', help='Require ordinary join-quest outcome using the separate incident fixture')
    parser.add_argument('--days', type=int, choices=range(0, 61), default=0, help='Additional ordinary survival days with living roster checks')
    parser.add_argument('--prepared-days', action='store_true', help='Evaluate existing native colony work plus shared food gathering; does not exercise full autonomous establishment')
    parser.add_argument('--timeout', type=int, default=2400)
    parser.add_argument('--matrix', action='store_true', help='Native reserve competition, cold-readiness refusal and emergency clock refusal')
    parser.add_argument('--emergency', action='store_true', help='Native incident and conservative 250-cell danger-stop profile only')
    args = parser.parse_args()
    if args.resume_trip and not (args.trip and args.shared and args.quest_trade and not args.diplomacy):
        parser.error('--resume-trip requires --trip --shared --quest-trade without --diplomacy')
    if args.matrix and not (args.trip and args.shared):
        parser.error('--matrix requires --trip --shared')
    if (args.logistics or args.multimap or args.diplomacy or args.quest_trade or args.recovery) and not (args.trip and args.shared):
        parser.error('Logistics, diplomacy, trade quests and multiple maps require --trip --shared')
    raise SystemExit(0 if run(args) else 1)

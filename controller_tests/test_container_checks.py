import importlib.util
from pathlib import Path
import subprocess
from types import SimpleNamespace

import pytest


spec = importlib.util.spec_from_file_location(
    'container_checks', Path(__file__).resolve().parents[1]/'scripts/container_checks.py')
checks = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checks)


@pytest.mark.parametrize('failure', [None, 'exit', 'no-tests', 'junit', 'cleanup', 'timeout'])
def test_selection_evidence_and_failures(monkeypatch, tmp_path, failure):
    calls = []
    monkeypatch.setattr(checks, 'docker_environment', lambda: ('docker', {}))

    def command(args, **kwargs):
        calls.append(args)
        if args[1:3] == ['image', 'inspect']:
            return SimpleNamespace(returncode=0, stdout='sha256:fixture\n')
        if args[1] == 'run':
            mount = args[args.index('--mount')+1]
            destination = Path(mount.split('source=', 1)[1].split(',target=')[0])
            if failure != 'junit':
                (destination/'junit.xml').write_text('<testsuite/>')
            if failure == 'timeout':
                raise subprocess.TimeoutExpired(args, 1)
            return SimpleNamespace(returncode={'exit': 1, 'no-tests': 5}.get(failure, 0))
        if args[1] == 'rm':
            return SimpleNamespace(returncode=1, stdout='',
                                   stderr='daemon unavailable' if failure == 'cleanup' else 'No such container')
        return SimpleNamespace(returncode=0)

    monkeypatch.setattr(checks.subprocess, 'run', command)
    report = checks.run(tmp_path/'results', tests=['controller_tests/test_container_worker.py'],
                        keyword='snapshot', controller_only=True)
    assert report['passed'] is (failure is None)
    assert len(report['workers']) == 1
    assert report['tests'] == ['controller_tests/test_container_worker.py']
    assert report['keyword'] == 'snapshot'
    assert report['build_target'] == 'controller-tests'
    assert report['dashboard_checked'] is False
    build = next(args for args in calls if args[1] == 'build')
    assert build[build.index('--target')+1] == 'controller-tests'
    run = next(args for args in calls if args[1] == 'run')
    assert 'sha256:fixture' in run
    assert run[-3:] == ['controller_tests/test_container_worker.py', '-k', 'snapshot']
    assert any(args[1] == 'rm' for args in calls)
    assert (tmp_path/'results/result.json').is_file()


@pytest.mark.parametrize('options', [{'workers': 0}, {'tests': ['--collect-only']},
                                    {'tests': ['controller_tests/../scripts']}])
def test_invalid_selection_does_not_start_run(tmp_path, options):
    with pytest.raises(ValueError):
        checks.run(tmp_path/'results', **options)
    assert not (tmp_path/'results').exists()

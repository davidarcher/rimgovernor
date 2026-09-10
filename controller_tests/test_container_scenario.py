import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts'))
import container_scenario as launcher


def test_standard_launcher_publishes_loopback_and_retains_url_and_cleanup(tmp_path, monkeypatch):
    calls = []
    def run(command, **kwargs):
        calls.append(command)
        text = ''
        if command[1:3] == ['image', 'inspect']:
            text = '1' if 'scenario-dashboard' in command[4] else 'sha256:fixture'
        if command[1] == 'port': text = '127.0.0.1:43210'
        if command[1] == 'wait': text = '0'
        return SimpleNamespace(stdout=text, stderr='', returncode=0)
    monkeypatch.setattr(launcher, 'docker_environment', lambda: ('docker', {}))
    monkeypatch.setattr(launcher.subprocess, 'run', run)
    args = SimpleNamespace(output=tmp_path/'out', game=tmp_path, mods=tmp_path, profile=tmp_path,
                           gabs=tmp_path, image='test', no_build=True, name='Winter campaign',
                           display='headless', timeout=60, command=['--', 'python', 'scripts/deterministic_foothold.py'])
    args.no_input_cache, args.worker_storage = True, 'bind'
    assert launcher.run(args)
    invocation = next(call for call in calls if call[1] == 'run')
    assert invocation[invocation.index('--publish')+1] == '127.0.0.1::8787'
    assert 'io.rimgovernor.colony.name=Winter campaign' in invocation
    assert json.loads((args.output/'dashboard.json').read_text())['dashboard_url'] == 'http://127.0.0.1:43210/scenario'
    assert calls[-1][1:3] == ['rm', '-f']


def test_old_images_fail_before_launch():
    with pytest.raises(ValueError, match='Rebuild'):
        launcher.require_dashboard_image(lambda *args, **kwargs: SimpleNamespace(stdout=''), 'old')


@pytest.mark.parametrize('exit_code,export_ok', [(0, True), (1, True), (0, False)])
def test_volume_runner_exports_before_cleanup_and_retains_failed_evidence(tmp_path, monkeypatch, exit_code, export_ok):
    calls = []
    def run(command, **kwargs):
        calls.append(command[1:])
        text = ''
        if command[1:3] == ['image', 'inspect']:
            text = '1' if 'scenario-dashboard' in command[4] else 'sha256:fixture'
        if command[1] == 'port': text = '127.0.0.1:43210'
        if command[1] == 'wait': text = str(exit_code)
        if command[1] == 'inspect': text = 'false'
        if command[1] == 'cp' and not export_ok: raise RuntimeError('export interrupted')
        return SimpleNamespace(stdout=text, stderr='', returncode=0)
    monkeypatch.setattr(launcher, 'docker_environment', lambda: ('docker', {}))
    monkeypatch.setattr(launcher.subprocess, 'run', run)
    monkeypatch.setattr(launcher, 'prepare_cache', lambda *args: {'volume': 'cached-input', 'key': 'fixed'})
    args = SimpleNamespace(output=tmp_path/'out', game=tmp_path, mods=tmp_path, profile=tmp_path,
        gabs=tmp_path, image='test', no_build=True, name='B04', display='headless', timeout=60,
        command=['python', 'probe.py'])
    assert launcher.run(args) is (exit_code == 0 and export_ok)
    result = json.loads((args.output/'result.json').read_text())
    assert result['storage']['retained'] is not (exit_code == 0 and export_ok)
    assert any(c[0] == 'cp' for c in calls)
    if export_ok:
        assert next(i for i,c in enumerate(calls) if c[0] == 'cp') < next(i for i,c in enumerate(calls) if c[0] == 'rm')
    else:
        assert result['container_retained'] and not any(c[0] == 'rm' for c in calls)


@pytest.mark.parametrize('name', ['container_population_acceptance', 'container_player_actions',
                                  'container_husbandry_acceptance', 'container_visual_acceptance'])
def test_specialized_launchers_use_standard_dashboard_contract(name):
    # Prevent new specialized launch paths from silently dropping discovery support.
    spec = importlib.util.find_spec(name)
    source = Path(spec.origin).read_text()
    assert 'dashboard_options(name' in source
    assert 'require_dashboard_image(' in source

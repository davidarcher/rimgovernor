from copy import deepcopy
import json
from pathlib import Path
import shutil
import subprocess

import pytest

from rimbot.campaign_manifest import capture_manifest, tracked_source
from rimbot.config import ModelRouting, ModelRole, Settings


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding='utf8')


def fixture(tmp_path):
    source, root, game = tmp_path/'source', tmp_path/'worker', tmp_path/'configured-game'
    write(source/'controller/example.py', 'original')
    metadata = '<ModMetaData><packageId>test.observations</packageId></ModMetaData>'
    write(source/'integrations/colony-bridge/About/About.xml', metadata)
    write(source/'integrations/colony-bridge/src/ColonyObservations.csproj',
          '<Project><PropertyGroup><AssemblyName>Test.Observations</AssemblyName></PropertyGroup></Project>')
    for args in (['init', '-q'], ['add', '.'],
                 ['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture']):
        subprocess.run(['git', '-C', str(source), *args], check=True, capture_output=True)
    write(game/'Mods/CustomFolder/About/About.xml', metadata)
    write(game/'Mods/CustomFolder/BridgeTools/Test.Observations.dll', 'production dll')
    for relative in ('headless-profile/Saves/RimBot-tribal8-baseline.rws',
                     'headless-profile/Config/Prefs.xml', 'headless-profile/Config/ModsConfig.xml',
                     'gabs/gabs-v1.1.1-windows-amd64/gabs.exe'):
        write(root/relative, relative)
    configuration = root/'config-headless'
    write(configuration/'config.json', json.dumps({'games': {'rimbot-trial': {'workingDir': str(game)}}}))
    routing = ModelRouting(roles={ModelRole.STRATEGIST: Settings(model='fixed-model')}).model_dump(mode='json')
    return source, root, configuration, routing


def test_isolated_paths_do_not_change_content_identity(tmp_path):
    source, root, config, routing = fixture(tmp_path)
    before = capture_manifest(source, root, config, routing)
    sibling = tmp_path/'other-worker'
    shutil.copytree(root, sibling)
    after = capture_manifest(source, sibling, sibling/'config-headless', routing)
    assert before['fingerprint'] == after['fingerprint']
    assert before['locations'] != after['locations']
    assert before['inputs']['source']['tracked_dirty'] is False
    assert before['inputs']['inference'] == routing


@pytest.mark.parametrize('changed', ['source', 'baseline_save', 'observations_dll', 'gabs',
                                    'profile_preferences', 'profile_mods', 'temperature'])
def test_input_changes_break_manifest_identity(tmp_path, changed):
    source, root, config, routing = fixture(tmp_path)
    before = capture_manifest(source, root, config, routing)
    if changed == 'source':
        write(source/'controller/example.py', 'modified')
    elif changed == 'temperature':
        routing = deepcopy(routing)
        routing['roles']['strategist']['temperature'] += .1
    else:
        write(Path(before['locations'][changed]), 'changed bytes')
    after = capture_manifest(source, root, config, routing)
    assert before['fingerprint'] != after['fingerprint']
    if changed == 'source':
        assert after['inputs']['source']['tracked_dirty'] is True
        assert after['inputs']['source']['revision'] == before['inputs']['source']['revision']
        assert after['inputs']['source']['content_sha256'] != before['inputs']['source']['content_sha256']


def test_missing_native_input_cannot_produce_manifest(tmp_path):
    source, root, config, routing = fixture(tmp_path)
    manifest = capture_manifest(source, root, config, routing)
    Path(manifest['locations']['observations_dll']).unlink()
    with pytest.raises(ValueError, match='production observation assembly'):
        capture_manifest(source, root, config, routing)


def test_tracked_deletion_changes_source_hash_but_ignored_outputs_do_not(tmp_path):
    source, *_ = fixture(tmp_path)
    before = tracked_source(source)
    write(source/'untracked.log', 'generated evidence')
    assert tracked_source(source) == before
    (source/'controller/example.py').unlink()
    after = tracked_source(source)
    assert after['tracked_dirty'] and after['content_sha256'] != before['content_sha256']

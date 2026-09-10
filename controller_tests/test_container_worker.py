import json
from pathlib import Path
import pytest
from rimbot.container_worker import stage
from rimbot.bridge import gabs_executable
from rimbot.config import Settings


def inputs(tmp_path):
    game, mods, profile = [tmp_path/name for name in ('game', 'mods', 'profile')]
    for root, name, content in [
        (game, 'RimWorldLinux', b'linux'),
        (mods, 'RimBotHeadless/Assemblies/HeadlessRimPatch.dll', b'first'),
        (mods, 'RimBridgeServer/About/About.xml', b'<ModMetaData/>'),
        (mods, 'RimBotObservations/About/About.xml', b'<ModMetaData/>'),
        (profile, 'Config/Prefs.xml', b'<Prefs/>'),
        (profile, 'Config/ModsConfig.xml', b'<ModsConfigData><activeMods><li>ludeon.rimworld</li></activeMods></ModsConfigData>'),
        (profile, 'Saves/RimBot-tribal8-baseline.rws', b'unchanged save'),
    ]:
        path = root/name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    gabs = tmp_path/'gabs'
    gabs.write_bytes(b'gabs')
    return game, mods, profile, gabs


def test_workers_snapshot_binaries_profiles_and_claims(tmp_path):
    sources = inputs(tmp_path)
    a, b = [stage(*sources, tmp_path/name) for name in ('a', 'b')]
    dll = Path('game/Mods/RimBotHeadless/Assemblies/HeadlessRimPatch.dll')
    (sources[1]/'RimBotHeadless/Assemblies/HeadlessRimPatch.dll').write_bytes(b'next build')
    assert (a/dll).read_bytes() == (b/dll).read_bytes() == b'first'
    (a/dll).write_bytes(b'worker change')
    assert (b/dll).read_bytes() == b'first'
    for root in (a, b):
        config = json.loads((root/'config-headless/config.json').read_text())
        game = config['games']['rimbot-trial']
        assert game['target'] == str(root/'game/RimWorldLinux')
        assert game['workingDir'] == str(root/'game')
        assert game['launchMode'] == 'DirectPath' and 'stopProcessName' not in game
        assert game['args'][0] == '-savedatafolder='+str(root/'headless-profile')
        assert gabs_executable(root) == root/'gabs/gabs'
        assert (root/'profile/Saves/RimBot-tribal8-baseline.rws').read_bytes() == b'unchanged save'
        assert str(dll).replace('\\', '/') in {key.replace('\\', '/') for key in json.loads((root/'inputs.json').read_text())}
    with pytest.raises((FileExistsError, ValueError)):
        stage(*sources, a)


def test_invalid_inputs_do_not_create_worker(tmp_path):
    sources = inputs(tmp_path)
    (sources[0]/'RimWorldLinux').unlink()
    with pytest.raises(ValueError, match='Missing Linux'):
        stage(*sources, tmp_path/'worker')
    assert not (tmp_path/'worker').exists()


def test_nested_worker_refused(tmp_path):
    sources = inputs(tmp_path)
    with pytest.raises(ValueError, match='separate'):
        stage(*sources, sources[0]/'worker')


def test_docker_model_requires_explicit_opt_in(monkeypatch):
    monkeypatch.delenv('RIMBOT_ALLOW_DOCKER_HOST_MODEL', raising=False)
    with pytest.raises(ValueError):
        Settings(model_url='http://host.docker.internal:1234/v1')
    monkeypatch.setenv('RIMBOT_ALLOW_DOCKER_HOST_MODEL', '1')
    assert Settings(model_url='http://host.docker.internal:1234/v1').model_url.endswith('/v1')
    for url in ['https://api.openai.com/v1', 'http://192.168.1.2:1234/v1',
                'http://host.docker.internal.evil/v1', 'http://user@host.docker.internal/v1']:
        with pytest.raises(ValueError):
            Settings(model_url=url)


def test_linux_gabs_is_private_in_cloned_profile(tmp_path):
    from rimbot.headless import isolated_root
    sources = inputs(tmp_path)
    root = stage(*sources, tmp_path/'original')
    clone = isolated_root(root, tmp_path/'clone')
    assert gabs_executable(clone) == clone/'gabs/gabs'
    gabs_executable(root).write_bytes(b'changed')
    assert gabs_executable(clone).read_bytes() == b'gabs'


def test_container_local_game_keeps_artifacts_external(tmp_path):
    sources = inputs(tmp_path)
    root = stage(*sources, tmp_path/'artifacts', tmp_path/'private-game')
    config = json.loads((root/'config/config.json').read_text())
    assert config['games']['rimbot-trial']['workingDir'] == str(tmp_path/'private-game')
    assert not (root/'game').exists()
    assert (root/'headless-profile/Saves/RimBot-tribal8-baseline.rws').is_file()
    assert 'game/RimWorldLinux' in json.loads((root/'inputs.json').read_text())
    with pytest.raises(ValueError, match='fresh'):
        stage(*sources, tmp_path/'another-root', tmp_path/'private-game')
    assert not (tmp_path/'another-root').exists()


def test_gc_mitigation_changes_only_private_boot_config(tmp_path):
    sources = inputs(tmp_path)
    boot = sources[0]/'RimWorldLinux_Data/boot.config'
    boot.parent.mkdir()
    original = b'other-setting=1\ngc-max-time-slice=3\n'
    boot.write_bytes(original)
    root = stage(*sources, tmp_path/'worker', unity_gc_time_slice=0)
    assert boot.read_bytes() == original
    assert (root/'game/RimWorldLinux_Data/boot.config').read_bytes() == original.replace(b'slice=3', b'slice=0')
    assert 'game/RimWorldLinux_Data/boot.config' in json.loads((root/'inputs.json').read_text())
    assert json.loads((root/'staging.json').read_text())['unity_gc_time_slice'] == 0
    untouched = stage(*sources, tmp_path/'unchanged')
    assert (untouched/'game/RimWorldLinux_Data/boot.config').read_bytes() == original
    boot.write_bytes(b'unrecognized=3')
    with pytest.raises(ValueError, match='existing Unity'):
        stage(*sources, tmp_path/'refused', unity_gc_time_slice=0)
    assert not (tmp_path/'refused').exists()


def test_rendered_worker_preserves_sources_and_uses_private_display(tmp_path):
    from rimbot.virtual_display import DisplaySettings
    sources = inputs(tmp_path)
    (sources[1]/'RimBotHeadless/Assemblies/HeadlessRimPatch.dll').unlink()
    original = (sources[2]/'Config/ModsConfig.xml').read_bytes()
    root = stage(*sources, tmp_path/'rendered', display=DisplaySettings.parse('1600x900'))
    args = json.loads((root/'config/config.json').read_text())['games']['rimbot-trial']['args']
    assert '-nographics' not in args and '-batchmode' not in args
    assert args[args.index('-screen-width')+1] == '1600'
    assert args[args.index('-screen-height')+1] == '900'
    assert '-force-glcore' in args
    import xml.etree.ElementTree as ET
    prefs = ET.parse(root/'profile/Config/Prefs.xml').getroot()
    assert prefs.findtext('screenWidth') == '1600'
    assert prefs.findtext('screenHeight') == '900'
    assert prefs.findtext('uiScale') == '1'
    assert not (root/'config-headless').exists()
    assert (sources[2]/'Config/ModsConfig.xml').read_bytes() == original
    assert json.loads((root/'staging.json').read_text())['display']['renderer'] == 'llvmpipe'


@pytest.mark.parametrize('resolution', ['0x720', '1280x0', '3841x2160', '640x2161', '1280;echo x', '-1x720'])
def test_display_rejects_invalid_dimensions(resolution):
    from rimbot.virtual_display import DisplaySettings
    with pytest.raises(ValueError):
        DisplaySettings.parse(resolution)

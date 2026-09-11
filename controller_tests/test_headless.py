import json
import xml.etree.ElementTree as ET
from rimgovernor.headless import prepare, prepare_rendered, isolated_root, require_native_package, NATIVE_PACKAGE
import pytest


def native_package(game):
    package=game/'Mods/RimGovernor'
    for path, content in [('About/About.xml', '<ModMetaData><packageId>'+NATIVE_PACKAGE+'</packageId></ModMetaData>'),
                          ('Assemblies/RimGovernor.Runtime.dll', 'runtime'),
                          ('BridgeTools/RimGovernor/RimGovernor.Bridge.dll', 'bridge')]:
        file=package/path;file.parent.mkdir(parents=True,exist_ok=True);file.write_text(content)


def test_headless_profile_leaves_interactive_configuration_unchanged(tmp_path):
    root=tmp_path/'bridge';game=tmp_path/'game'
    native_package(game)
    (root/'config').mkdir(parents=True)
    original=json.dumps({'games':{'rimgovernor-trial':{'launchMode':'DirectPath',
        'stopProcessName':'RimWorldWin64.exe','workingDir':str(game),'args':['original']}}})
    (root/'config/config.json').write_text(original)
    (root/'profile/Config').mkdir(parents=True)
    (root/'profile/Saves').mkdir()
    mods='<ModsConfigData><activeMods><li>brrainz.harmony</li></activeMods></ModsConfigData>'
    (root/'profile/Config/ModsConfig.xml').write_text(mods)
    (root/'profile/Config/Prefs.xml').write_text('<Prefs/>')
    (root/'profile/Saves/RimGovernor-tribal8-baseline.rws').write_text('<savegame/>')
    for _ in range(2):destination=prepare(root)
    assert (root/'config/config.json').read_text()==original
    assert (root/'profile/Config/ModsConfig.xml').read_text()==mods
    args=json.loads((destination/'config.json').read_text())['games']['rimgovernor-trial']['args']
    assert '-batchmode' in args and '-nographics' in args
    assert '-rimgovernor-pause-on-load' in args
    assert 'stopProcessName' not in json.loads((destination/'config.json').read_text())['games']['rimgovernor-trial']
    assert str(root/'headless-profile') in args[0]
    active=ET.parse(root/'headless-profile/Config/ModsConfig.xml').getroot().find('activeMods')
    assert [n.text for n in active].count(NATIVE_PACKAGE)==1


def test_parallel_roots_have_private_profiles_and_no_process_name_fallback(tmp_path):
    source=tmp_path/'source';(source/'config').mkdir(parents=True)
    config={'games':{'rimgovernor-trial':{'launchMode':'DirectPath','stopProcessName':'RimWorldWin64.exe'}}}
    (source/'config/config.json').write_text(json.dumps(config))
    paths=['profile/Config/Prefs.xml','profile/Config/ModsConfig.xml',
        'profile/Saves/RimGovernor-tribal8-baseline.rws','gabs/gabs-v1.1.1-windows-amd64/gabs.exe']
    for relative in paths:
        p=source/relative;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('fixture')
    a=isolated_root(source,tmp_path/'a');b=isolated_root(source,tmp_path/'b')
    (a/paths[0]).write_text('changed')
    assert (b/paths[0]).read_text()=='fixture'
    assert (source/paths[0]).read_text()=='fixture'
    assert 'stopProcessName' not in json.loads((a/'config/config.json').read_text())['games']['rimgovernor-trial']
    assert json.loads((source/'config/config.json').read_text())==config
    with pytest.raises(ValueError,match='already exists'):isolated_root(source,a)


@pytest.mark.parametrize('mode', [None, 'Steam', ''])
@pytest.mark.parametrize('operation', ['headless', 'rendered', 'isolated'])
def test_disposable_profiles_reject_unowned_launches_before_writing(tmp_path, mode, operation):
    source=tmp_path/'source';(source/'config').mkdir(parents=True)
    game={'stopProcessName':'RimWorldWin64.exe'}
    if mode is not None:game['launchMode']=mode
    original=json.dumps({'games':{'rimgovernor-trial':game}})
    (source/'config/config.json').write_text(original)
    destination=tmp_path/'worker'
    with pytest.raises(ValueError,match='DirectPath PID-owned'):
        if operation == 'headless':prepare(source)
        elif operation == 'rendered':prepare_rendered(source)
        else:isolated_root(source,destination)
    assert (source/'config/config.json').read_text()==original
    assert not destination.exists()
    assert not (source/'headless-profile').exists()
    assert not (source/'config-headless').exists()


def test_rendered_worker_uses_private_profile_without_headless_patches(tmp_path):
    root=tmp_path/'worker';(root/'config').mkdir(parents=True)
    native_package(tmp_path/'game')
    (root/'profile/Config').mkdir(parents=True)
    (root/'config/config.json').write_text(json.dumps({'games':{'rimgovernor-trial':{
        'launchMode':'DirectPath','stopProcessName':'RimWorldWin64.exe','workingDir':str(tmp_path/'game'),
        'args':['-savedatafolder=shared','-batchmode','-nographics']}}}))
    (root/'profile/Config/ModsConfig.xml').write_text(
        '<ModsConfigData><activeMods><li>brrainz.harmony</li><li>redeyedev.headlessrim</li></activeMods></ModsConfigData>')
    destination=prepare_rendered(root)
    game=json.loads((destination/'config.json').read_text())['games']['rimgovernor-trial']
    assert game['args'][0]=='-savedatafolder='+str(root/'profile')
    assert '-batchmode' not in game['args'] and '-nographics' not in game['args']
    assert '-rimgovernor-pause-on-load' in game['args']
    assert 'stopProcessName' not in game
    assert [r.text for r in ET.parse(root/'profile/Config/ModsConfig.xml').getroot().find('activeMods')]==['brrainz.harmony', 'brrainz.rimbridgeserver', NATIVE_PACKAGE]


def test_fresh_profile_strips_split_package_ids_and_deduplicates_unified_mod(tmp_path):
    from rimgovernor.headless import prepare_native_mod_config
    path=tmp_path/'ModsConfig.xml'
    path.write_text('<ModsConfigData><activeMods><li>ludeon.rimworld</li><li>RedEyeDev.HeadlessRim</li>'
                    '<li>davidarcher.rimgovernor.observations</li><li>'+NATIVE_PACKAGE+'</li>'
                    '<li>'+NATIVE_PACKAGE+'</li></activeMods></ModsConfigData>')
    for _ in range(2):prepare_native_mod_config(path)
    assert [n.text for n in ET.parse(path).getroot().find('activeMods')]==['ludeon.rimworld', 'brrainz.harmony', 'brrainz.rimbridgeserver', NATIVE_PACKAGE]


@pytest.mark.parametrize('missing', ['Assemblies/RimGovernor.Runtime.dll', 'BridgeTools/RimGovernor/RimGovernor.Bridge.dll'])
def test_native_package_requires_both_assemblies(tmp_path, missing):
    native_package(tmp_path)
    (tmp_path/'Mods/RimGovernor'/missing).unlink()
    with pytest.raises(ValueError,match='Missing unified native input'):
        require_native_package(tmp_path/'Mods')


def test_native_package_rejects_split_installation_before_staging(tmp_path):
    native_package(tmp_path)
    old=tmp_path/'Mods/Old/About/About.xml';old.parent.mkdir(parents=True)
    old.write_text('<ModMetaData><packageId>RedEyeDev.HeadlessRim</packageId></ModMetaData>')
    with pytest.raises(ValueError,match='Remove split native packages'):
        require_native_package(tmp_path/'Mods')


def test_native_package_rejects_duplicate_unified_installations(tmp_path):
    native_package(tmp_path)
    duplicate=tmp_path/'Mods/Other/About/About.xml';duplicate.parent.mkdir(parents=True)
    duplicate.write_text('<ModMetaData><packageId>'+NATIVE_PACKAGE+'</packageId></ModMetaData>')
    with pytest.raises(ValueError,match='exactly one unified native package'):
        require_native_package(tmp_path/'Mods')


def test_fresh_trial_copies_supplied_native_save_without_legacy_rewriting(tmp_path):
    import hashlib
    import importlib.util
    from pathlib import Path
    spec=importlib.util.spec_from_file_location('prepare_bridge_trial', Path(__file__).parents[1]/'scripts/prepare_bridge_trial.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    game=tmp_path/'game';native_package(game)
    (game/'RimWorldWin64.exe').touch()
    sdk=game/'Mods/RimBridgeServer/About/About.xml';sdk.parent.mkdir(parents=True);sdk.write_text('<ModMetaData/>')
    source=tmp_path/'source';(source/'Config').mkdir(parents=True);(source/'Saves').mkdir()
    (source/'Config/Prefs.xml').write_text('<Prefs/>')
    (source/'Config/ModsConfig.xml').write_text('<ModsConfigData><activeMods><li>ludeon.rimworld</li>'
        '<li>brrainz.harmony</li><li>davidarcher.rimgovernor.observations</li></activeMods></ModsConfigData>')
    saved=b'<?xml version="1.0" encoding="utf-8"?><savegame><game>fresh native game</game></savegame>'
    save=source/'Saves/RimGovernor-tribal8-baseline.rws';save.write_bytes(saved)
    root=tmp_path/'worker'
    module.prepare(source, game, root, observations=True)
    assert (root/'profile/Saves'/save.name).read_bytes()==saved
    assert save.read_bytes()==saved
    assert json.loads((root/'fixture.json').read_text())['source_sha256']==hashlib.sha256(saved).hexdigest()
    assert [n.text for n in ET.parse(root/'profile/Config/ModsConfig.xml').getroot().find('activeMods')]==[
        'ludeon.rimworld','brrainz.harmony','brrainz.rimbridgeserver',NATIVE_PACKAGE]

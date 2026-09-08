import json
import xml.etree.ElementTree as ET
from rimbot.headless import prepare, isolated_root
import pytest


def test_headless_profile_leaves_interactive_configuration_unchanged(tmp_path):
    root=tmp_path/'bridge';game=tmp_path/'game'
    dll=game/'Mods/RimBotHeadless/Assemblies/HeadlessRimPatch.dll'
    dll.parent.mkdir(parents=True);dll.touch()
    (root/'config').mkdir(parents=True)
    original=json.dumps({'games':{'rimbot-trial':{'launchMode':'DirectPath',
        'stopProcessName':'RimWorldWin64.exe','workingDir':str(game),'args':['original']}}})
    (root/'config/config.json').write_text(original)
    (root/'profile/Config').mkdir(parents=True)
    (root/'profile/Saves').mkdir()
    mods='<ModsConfigData><activeMods><li>brrainz.harmony</li></activeMods></ModsConfigData>'
    (root/'profile/Config/ModsConfig.xml').write_text(mods)
    (root/'profile/Config/Prefs.xml').write_text('<Prefs/>')
    (root/'profile/Saves/RimBot-tribal8-baseline.rws').write_text('<savegame/>')
    for _ in range(2):destination=prepare(root)
    assert (root/'config/config.json').read_text()==original
    assert (root/'profile/Config/ModsConfig.xml').read_text()==mods
    args=json.loads((destination/'config.json').read_text())['games']['rimbot-trial']['args']
    assert '-batchmode' in args and '-nographics' in args
    assert 'stopProcessName' not in json.loads((destination/'config.json').read_text())['games']['rimbot-trial']
    assert str(root/'headless-profile') in args[0]
    active=ET.parse(root/'headless-profile/Config/ModsConfig.xml').getroot().find('activeMods')
    assert [n.text for n in active].count('redeyedev.headlessrim')==1


def test_parallel_roots_have_private_profiles_and_no_process_name_fallback(tmp_path):
    source=tmp_path/'source';(source/'config').mkdir(parents=True)
    config={'games':{'rimbot-trial':{'launchMode':'DirectPath','stopProcessName':'RimWorldWin64.exe'}}}
    (source/'config/config.json').write_text(json.dumps(config))
    paths=['profile/Config/Prefs.xml','profile/Config/ModsConfig.xml',
        'profile/Saves/RimBot-tribal8-baseline.rws','gabs/gabs-v1.1.1-windows-amd64/gabs.exe']
    for relative in paths:
        p=source/relative;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('fixture')
    a=isolated_root(source,tmp_path/'a');b=isolated_root(source,tmp_path/'b')
    (a/paths[0]).write_text('changed')
    assert (b/paths[0]).read_text()=='fixture'
    assert (source/paths[0]).read_text()=='fixture'
    assert 'stopProcessName' not in json.loads((a/'config/config.json').read_text())['games']['rimbot-trial']
    assert json.loads((source/'config/config.json').read_text())==config
    with pytest.raises(ValueError,match='already exists'):isolated_root(source,a)


@pytest.mark.parametrize('mode', [None, 'Steam', ''])
@pytest.mark.parametrize('operation', ['headless', 'isolated'])
def test_disposable_profiles_reject_unowned_launches_before_writing(tmp_path, mode, operation):
    source=tmp_path/'source';(source/'config').mkdir(parents=True)
    game={'stopProcessName':'RimWorldWin64.exe'}
    if mode is not None:game['launchMode']=mode
    original=json.dumps({'games':{'rimbot-trial':game}})
    (source/'config/config.json').write_text(original)
    destination=tmp_path/'worker'
    with pytest.raises(ValueError,match='DirectPath PID-owned'):
        if operation == 'headless':prepare(source)
        else:isolated_root(source,destination)
    assert (source/'config/config.json').read_text()==original
    assert not destination.exists()
    assert not (source/'headless-profile').exists()
    assert not (source/'config-headless').exists()

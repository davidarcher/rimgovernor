import json
import xml.etree.ElementTree as ET
from rimbot.headless import prepare


def test_headless_profile_leaves_interactive_configuration_unchanged(tmp_path):
    root=tmp_path/'bridge';game=tmp_path/'game'
    dll=game/'Mods/RimBotHeadless/Assemblies/HeadlessRimPatch.dll'
    dll.parent.mkdir(parents=True);dll.touch()
    (root/'config').mkdir(parents=True)
    original=json.dumps({'games':{'rimbot-trial':{'workingDir':str(game),'args':['original']}}})
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
    assert str(root/'headless-profile') in args[0]
    active=ET.parse(root/'headless-profile/Config/ModsConfig.xml').getroot().find('activeMods')
    assert [n.text for n in active].count('redeyedev.headlessrim')==1

"""Separate disposable profile for no-graphics native bridge tests."""
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path


def prepare(root):
    root=Path(root).resolve()
    config=json.loads((root/'config/config.json').read_text(encoding='utf8'))
    game=config['games']['rimbot-trial']
    installed=Path(game['workingDir'])/'Mods/RimBotHeadless/Assemblies/HeadlessRimPatch.dll'
    if not installed.is_file():
        raise ValueError('Build/install the headless test mod with scripts/build_headless.ps1 -Install first')
    profile=root/'headless-profile'
    (profile/'Config').mkdir(parents=True,exist_ok=True)
    (profile/'Saves').mkdir(exist_ok=True)
    for name in ('Prefs.xml','ModsConfig.xml'):
        shutil.copy2(root/'profile/Config'/name,profile/'Config'/name)
    mods=ET.parse(profile/'Config/ModsConfig.xml')
    active=mods.getroot().find('activeMods')
    if not any((item.text or '').lower()=='redeyedev.headlessrim' for item in active):
        ET.SubElement(active,'li').text='redeyedev.headlessrim'
    mods.write(profile/'Config/ModsConfig.xml',encoding='utf8',xml_declaration=True)
    baseline='RimBot-tribal8-baseline.rws'
    shutil.copy2(root/'profile/Saves'/baseline,profile/'Saves'/baseline)
    game['args']=['-savedatafolder='+str(profile),'-logFile',str(root/'HeadlessPlayer.log'),'-batchmode','-nographics']
    destination=root/'config-headless'
    destination.mkdir(exist_ok=True)
    (destination/'config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    return destination

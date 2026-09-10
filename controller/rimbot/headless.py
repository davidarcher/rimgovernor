"""Separate disposable profile for no-graphics native bridge tests."""
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path


def _require_owned_launch(game):
    if game.get('launchMode') != 'DirectPath':
        raise ValueError('Disposable profiles require DirectPath PID-owned launches')
    # Let GABS use its recorded process identity; never fall back to all games
    # with the same executable name when that identity is unavailable.
    game.pop('stopProcessName', None)


def isolated_root(source, destination):
    """Create a fresh worker root; never share GABS claims or writable saves."""
    source, destination=Path(source).resolve(),Path(destination).resolve()
    if destination.exists():
        raise ValueError('Worker root already exists; use a fresh directory')
    config=json.loads((source/'config/config.json').read_text(encoding='utf8'))
    game=config['games']['rimbot-trial']
    _require_owned_launch(game)
    from .bridge import gabs_executable
    binary = gabs_executable(source)
    relative = binary.relative_to(source) if binary.is_relative_to(source) else Path('gabs')/binary.name
    config.setdefault('rimbot', {})['gabsExecutable'] = relative.as_posix()
    (destination/relative).parent.mkdir(parents=True)
    shutil.copy2(binary, destination/relative)
    (destination/'config').mkdir(parents=True)
    (destination/'config/config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    for relative in ('profile/Config/Prefs.xml','profile/Config/ModsConfig.xml',
                     'profile/Saves/RimBot-tribal8-baseline.rws'):
        target=destination/relative;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(source/relative,target)
    return destination


def prepare_rendered(root):
    """Launch the disposable profile visibly, without headless patches or flags."""
    root = Path(root).resolve()
    configuration = root/'config'
    config = json.loads((configuration/'config.json').read_text(encoding='utf8'))
    game = config['games']['rimbot-trial']
    _require_owned_launch(game)
    profile = root/'profile'
    mods = ET.parse(profile/'Config/ModsConfig.xml')
    active = mods.getroot().find('activeMods')
    for item in list(active):
        if (item.text or '').lower() == 'redeyedev.headlessrim':
            active.remove(item)
    mods.write(profile/'Config/ModsConfig.xml', encoding='utf8', xml_declaration=True)
    game['args'] = ['-savedatafolder='+str(profile), '-logFile', str(root/'Player.log'),
                    '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-rimbot-pause-on-load']
    (configuration/'config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    return configuration


def prepare(root):
    root=Path(root).resolve()
    config=json.loads((root/'config/config.json').read_text(encoding='utf8'))
    game=config['games']['rimbot-trial']
    _require_owned_launch(game)
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
    game['args']=['-savedatafolder='+str(profile),'-logFile',str(root/'HeadlessPlayer.log'),'-batchmode','-nographics','-rimbot-pause-on-load']
    destination=root/'config-headless'
    destination.mkdir(exist_ok=True)
    (destination/'config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    return destination


def rendered_headless_mismatch(root):
    """Allow only the known render-only mod difference in our prepared baseline."""
    root=Path(root)
    try:
        saved=ET.parse(root/'profile/Saves/RimBot-tribal8-baseline.rws').getroot()
        active=ET.parse(root/'profile/Config/ModsConfig.xml').getroot()
        required={n.text.casefold() for n in saved.findall('./meta/modIds/li') if n.text}
        enabled={n.text.casefold() for n in active.findall('./activeMods/li') if n.text}
        return bool(required) and required-enabled=={'redeyedev.headlessrim'}
    except (OSError,ET.ParseError):
        return False

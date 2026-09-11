"""Separate disposable profile for no-graphics native bridge tests."""
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

NATIVE_PACKAGE = 'davidarcher.rimgovernor.native'
LEGACY_PACKAGES = frozenset(('davidarcher.rimgovernor.observations', 'redeyedev.headlessrim'))


def require_native_package(mods: Path) -> None:
    """Check the complete unified package before preparing a private worker."""
    package = mods/'RimGovernor'
    for relative in ('About/About.xml', 'Assemblies/RimGovernor.Runtime.dll',
                     'BridgeTools/RimGovernor/RimGovernor.Bridge.dll'):
        if not (package/relative).is_file():
            raise ValueError('Missing unified native input: '+str(package/relative))
    if (ET.parse(package/'About/About.xml').getroot().findtext('packageId') or '').casefold() != NATIVE_PACKAGE:
        raise ValueError('RimGovernor package metadata does not identify the unified native mod')
    unified = 0
    for metadata in mods.glob('*/About/About.xml'):
        identity = (ET.parse(metadata).getroot().findtext('packageId') or '').casefold()
        unified += identity == NATIVE_PACKAGE
        if identity in LEGACY_PACKAGES:
            raise ValueError('Remove split native packages from fresh worker inputs: '+str(metadata.parent.parent))
    if unified != 1:
        raise ValueError('Fresh worker inputs require exactly one unified native package')


def prepare_native_mod_config(path: Path) -> None:
    """Enable the same package in normal and batch profiles, without split mods."""
    mods = ET.parse(path)
    active = mods.getroot().find('activeMods')
    if active is None:
        raise ValueError('Native profile is missing activeMods')
    required = ('brrainz.harmony', 'brrainz.rimbridgeserver', NATIVE_PACKAGE)
    for item in list(active):
        if (item.text or '').casefold() in LEGACY_PACKAGES | set(required):
            active.remove(item)
    for identity in required:
        ET.SubElement(active, 'li').text = identity
    mods.write(path, encoding='utf8', xml_declaration=True)


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
    game=config['games']['rimgovernor-trial']
    _require_owned_launch(game)
    from .bridge import gabs_executable
    binary = gabs_executable(source)
    relative = binary.relative_to(source) if binary.is_relative_to(source) else Path('gabs')/binary.name
    config.setdefault('rimgovernor', {})['gabsExecutable'] = relative.as_posix()
    (destination/relative).parent.mkdir(parents=True)
    shutil.copy2(binary, destination/relative)
    (destination/'config').mkdir(parents=True)
    (destination/'config/config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    for relative in ('profile/Config/Prefs.xml','profile/Config/ModsConfig.xml',
                     'profile/Saves/RimGovernor-tribal8-baseline.rws'):
        target=destination/relative;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(source/relative,target)
    return destination


def prepare_rendered(root):
    """Launch the unified native package without batch-mode flags."""
    root = Path(root).resolve()
    configuration = root/'config'
    config = json.loads((configuration/'config.json').read_text(encoding='utf8'))
    game = config['games']['rimgovernor-trial']
    _require_owned_launch(game)
    require_native_package(Path(game['workingDir'])/'Mods')
    profile = root/'profile'
    prepare_native_mod_config(profile/'Config/ModsConfig.xml')
    game['args'] = ['-savedatafolder='+str(profile), '-logFile', str(root/'Player.log'),
                    '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-rimgovernor-pause-on-load']
    (configuration/'config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    return configuration


def prepare(root):
    root=Path(root).resolve()
    config=json.loads((root/'config/config.json').read_text(encoding='utf8'))
    game=config['games']['rimgovernor-trial']
    _require_owned_launch(game)
    require_native_package(Path(game['workingDir'])/'Mods')
    profile=root/'headless-profile'
    (profile/'Config').mkdir(parents=True,exist_ok=True)
    (profile/'Saves').mkdir(exist_ok=True)
    for name in ('Prefs.xml','ModsConfig.xml'):
        shutil.copy2(root/'profile/Config'/name,profile/'Config'/name)
    prepare_native_mod_config(profile/'Config/ModsConfig.xml')
    baseline='RimGovernor-tribal8-baseline.rws'
    shutil.copy2(root/'profile/Saves'/baseline,profile/'Saves'/baseline)
    game['args']=['-savedatafolder='+str(profile),'-logFile',str(root/'HeadlessPlayer.log'),'-batchmode','-nographics','-rimgovernor-pause-on-load']
    destination=root/'config-headless'
    destination.mkdir(exist_ok=True)
    (destination/'config.json').write_text(json.dumps(config,indent=2),encoding='utf8')
    return destination

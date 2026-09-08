"""Content provenance for fixed-source, fixed-inference campaign comparisons."""
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def tracked_source(source):
    source = Path(source).resolve()
    def git(*args):
        return subprocess.check_output(['git', '-C', str(source), *args])
    names = git('ls-files', '-z', '--cached').decode('utf8').split('\0')
    rows = []
    for name in sorted(set(names) - {''}):
        path = source/name
        rows.append([name, file_hash(path) if path.is_file() else None])
    status = git('status', '--porcelain=v1', '--untracked-files=no').decode('utf8').splitlines()
    return dict(revision=git('rev-parse', 'HEAD').decode().strip(),
                tracked_dirty=bool(status), tracked_status=status,
                tracked_files=len(rows), content_sha256=_digest(rows))


def capture_manifest(source, worker_root, configuration, routing):
    """Hash actual prepared inputs before startup; missing inputs fail closed.

    Locations are evidence only, excluded from comparison so isolated worker
    directory names do not make otherwise identical campaigns differ.
    """
    source, root, configuration = Path(source).resolve(), Path(worker_root).resolve(), Path(configuration).resolve()
    game = json.loads((configuration/'config.json').read_text(encoding='utf8'))['games']['rimbot-trial']
    game_root = Path(game['workingDir'])
    if not game_root.is_absolute():
        game_root = (configuration/game_root).resolve()
    about = ET.parse(source/'integrations/colony-bridge/About/About.xml').getroot()
    package = about.findtext('packageId')
    assembly = ET.parse(source/'integrations/colony-bridge/src/ColonyObservations.csproj').getroot().findtext('PropertyGroup/AssemblyName')
    if not package or not assembly:
        raise ValueError('Observation package/assembly identity is unavailable')
    candidates = []
    for metadata in (game_root/'Mods').glob('*/About/About.xml'):
        try:
            installed_package = ET.parse(metadata).getroot().findtext('packageId')
        except ET.ParseError:
            continue
        if installed_package and installed_package.casefold() == package.casefold():
            candidates.extend(metadata.parent.parent.rglob(assembly+'.dll'))
    if len(candidates) != 1:
        raise ValueError('Expected one installed production observation assembly, found '+str(len(candidates)))
    paths = {
        'baseline_save': root/'headless-profile/Saves/RimBot-tribal8-baseline.rws',
        'profile_preferences': root/'headless-profile/Config/Prefs.xml',
        'profile_mods': root/'headless-profile/Config/ModsConfig.xml',
        'gabs': root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',
        'observations_dll': candidates[0],
    }
    inputs = dict(version=1, source=tracked_source(source), inference=routing,
                  observations_package=package, observations_assembly=assembly,
                  artifacts={key: file_hash(path) for key, path in paths.items()})
    return dict(fingerprint=_digest(inputs), inputs=inputs,
                locations={key: str(path.resolve()) for key, path in paths.items()},
                scope='Tracked working-tree bytes, effective inference configuration, prepared fixture/profile, GABS and installed observation assembly. Model weight bytes and other installed mods are not fingerprinted.')

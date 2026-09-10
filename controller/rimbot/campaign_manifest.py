"""Content provenance for fixed-source, fixed-inference campaign comparisons."""
from .bridge import gabs_executable
import hashlib
import json
from pathlib import Path
import subprocess
import os
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
    if os.environ.get('RIMBOT_CONTAINER_SOURCE') == '1' and not (source/'.git').exists():
        rows=[]
        for directory in ('controller','scripts','integrations','third_party'):
            for path in sorted((source/directory).rglob('*')):
                if (path.is_file() and path.suffix.lower() in {'.py','.cs','.ps1','.ts','.tsx','.js','.json','.toml','.csproj','.xml'}
                        and not {'__pycache__','obj','bin','node_modules'} & set(path.relative_to(source).parts)):
                    rows.append([path.relative_to(source).as_posix(),file_hash(path)])
        for name in ('pyproject.toml','THIRD_PARTY.md'):
            rows.append([name,file_hash(source/name)])
        if not rows or not (source/'controller/rimbot/bridge_runtime.py').is_file():
            raise ValueError('Packaged controller source is missing')
        return dict(revision=None,tracked_dirty=None,tracked_status=None,
                    tracked_files=len(rows),untracked_code=None,content_sha256=_digest(rows),
                    mode='container_source_bytes',files=rows)
    def git(*args):
        return subprocess.check_output(['git', '-C', str(source), *args])
    names = git('ls-files', '-z', '--cached').decode('utf8').split('\0')
    rows = []
    for name in sorted(set(names) - {''}):
        path = source/name
        rows.append([name, file_hash(path) if path.is_file() else None])
    status = git('status', '--porcelain=v1', '--untracked-files=no').decode('utf8').splitlines()
    untracked = sorted(name for name in git('ls-files','-z','--others','--exclude-standard').decode('utf8').split('\0')
        if name and Path(name).suffix.lower() in {'.py','.cs','.ps1','.ts','.tsx','.js','.json','.toml','.csproj'})
    additional = [[name,file_hash(source/name)] for name in untracked]
    return dict(revision=git('rev-parse', 'HEAD').decode().strip(),
                tracked_dirty=bool(status), tracked_status=status,
                tracked_files=len(rows), untracked_code=untracked, content_sha256=_digest(rows+additional))


def snapshot_source(source):
    """Fingerprint an explicit packaged source tree without claiming Git identity."""
    source = Path(source).resolve()
    roots = ['controller', 'scripts', 'integrations', 'third_party']
    paths = [source/'pyproject.toml', source/'THIRD_PARTY.md']
    for name in roots:
        root = source/name
        if not root.is_dir():
            raise ValueError('Packaged source directory is missing: '+name)
        paths.extend(p for p in root.rglob('*') if p.is_file()
                     and not {'__pycache__', 'obj', 'bin', '.pytest_cache'} & set(p.relative_to(root).parts))
    rows = [[p.relative_to(source).as_posix(), file_hash(p)] for p in sorted(paths)]
    return dict(revision=None, tracked_dirty=None, snapshot_files=len(rows),
                content_sha256=_digest(rows), scope='Packaged source bytes; Git metadata unavailable')


def capture_manifest(source, worker_root, configuration, routing, *, profile=None, source_snapshot=False):
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
    identity_candidates = []
    identity_assembly = ET.parse(source/'integrations/colony-bridge/src/identity/ColonyIdentity.csproj').getroot().findtext('PropertyGroup/AssemblyName')
    if not identity_assembly:
        raise ValueError('Colony identity assembly name is unavailable')
    for metadata in (game_root/'Mods').glob('*/About/About.xml'):
        try:
            installed_package = ET.parse(metadata).getroot().findtext('packageId')
        except ET.ParseError:
            continue
        if installed_package and installed_package.casefold() == package.casefold():
            candidates.extend(metadata.parent.parent.rglob(assembly+'.dll'))
            identity_candidates.extend(metadata.parent.parent.rglob(identity_assembly+'.dll'))
    if len(candidates) != 1:
        raise ValueError('Expected one installed production observation assembly, found '+str(len(candidates)))
    profile = Path(profile) if profile is not None else root/'headless-profile'
    if len(identity_candidates) != 1:
        raise ValueError('Expected one installed colony identity assembly')
    paths = {
        'baseline_save': profile/'Saves/RimBot-tribal8-baseline.rws',
        'profile_preferences': profile/'Config/Prefs.xml',
        'profile_mods': profile/'Config/ModsConfig.xml',
        'gabs': gabs_executable(root, configuration),
        'observations_dll': candidates[0],
        'identity_dll': identity_candidates[0],
    }
    if '-nographics' in game.get('args',[]):
        paths['headless_dll']=game_root/'Mods/RimBotHeadless/Assemblies/HeadlessRimPatch.dll'
    inputs = dict(version=3, source=snapshot_source(source) if source_snapshot else tracked_source(source), inference=routing,
                  observations_package=package, observations_assembly=assembly,
                  artifacts={key: file_hash(path) for key, path in paths.items()})
    return dict(fingerprint=_digest(inputs), inputs=inputs,
                locations={key: str(path.resolve()) for key, path in paths.items()},
                scope='Tracked working-tree bytes and listed untracked code, effective inference configuration, prepared fixture/profile, GABS, installed observation and colony identity assemblies, and headless assembly for no-graphics launches. Model weight bytes and other installed mods are not fingerprinted.')

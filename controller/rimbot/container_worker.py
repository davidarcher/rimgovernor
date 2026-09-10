"""Stage immutable licensed inputs into a fresh, private container worker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil

from .headless import prepare


def stage(game, mods, profile, gabs, root):
    game, mods, profile, gabs, root = [Path(p).resolve() for p in (game, mods, profile, gabs, root)]
    for source in (game, mods, profile, gabs):
        if source == root or source in root.parents or root in source.parents:
            raise ValueError('Worker output must be separate from every input')
    required = [game/'RimWorldLinux', gabs, profile/'Config/Prefs.xml',
                profile/'Config/ModsConfig.xml', profile/'Saves/RimBot-tribal8-baseline.rws',
                mods/'RimBotHeadless/Assemblies/HeadlessRimPatch.dll',
                mods/'RimBridgeServer/About/About.xml', mods/'RimBotObservations/About/About.xml']
    for path in required:
        if not path.is_file():
            raise ValueError(f'Missing Linux worker input: {path}')
    root.mkdir(parents=True, exist_ok=False)
    # Dereference input links so running workers cannot observe later DLL changes.
    shutil.copytree(game, root/'game', ignore=shutil.ignore_patterns('Mods'))
    shutil.copytree(mods, root/'game/Mods')
    (root/'profile/Config').mkdir(parents=True)
    (root/'profile/Saves').mkdir()
    for relative in ('Config/Prefs.xml', 'Config/ModsConfig.xml', 'Saves/RimBot-tribal8-baseline.rws'):
        shutil.copy2(profile/relative, root/'profile'/relative)
    (root/'gabs').mkdir()
    shutil.copy2(gabs, root/'gabs/gabs')
    for executable in (root/'game/RimWorldLinux', root/'gabs/gabs'):
        executable.chmod(executable.stat().st_mode | 0o111)
    config = {'version': '1.0', 'rimbot': {'gabsExecutable': 'gabs/gabs'}, 'games': {
        'rimbot-trial': {'id': 'rimbot-trial', 'name': 'RimBot container worker',
                        'launchMode': 'DirectPath', 'target': str(root/'game/RimWorldLinux'),
                        'workingDir': str(root/'game'), 'args': []}}}
    (root/'config').mkdir()
    (root/'config/config.json').write_text(json.dumps(config, indent=2), encoding='utf8')
    prepare(root)
    files = [root/'gabs/gabs', root/'game/RimWorldLinux', *sorted((root/'game/Mods').rglob('*.dll')),
             root/'profile/Saves/RimBot-tribal8-baseline.rws', *sorted((root/'profile/Config').glob('*.xml'))]
    hashes = {}
    for path in files:
        with path.open('rb') as stream:
            hashes[str(path.relative_to(root))] = hashlib.file_digest(stream, 'sha256').hexdigest()
    (root/'inputs.json').write_text(json.dumps(hashes, indent=2), encoding='utf8')
    return root


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', default='/inputs/game')
    parser.add_argument('--mods', default='/inputs/mods')
    parser.add_argument('--profile', default='/inputs/profile')
    parser.add_argument('--gabs', default='/inputs/gabs/gabs')
    parser.add_argument('--root', default='/worker/run')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    root = stage(args.game, args.mods, args.profile, args.gabs, args.root)
    os.environ.update(RIMBOT_BRIDGE_ROOT=str(root), RIMBOT_DATA=str(root/'data'),
                      RIMBOT_HEADLESS='1', RIMBOT_BRIDGE_FRESH='1')
    command = args.command or ['python', '-m', 'rimbot', '--host', '0.0.0.0']
    if command[0] == '--':
        command = command[1:]
    os.execvp(command[0], command)


if __name__ == '__main__':
    main()

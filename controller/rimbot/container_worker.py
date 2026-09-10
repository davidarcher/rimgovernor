"""Stage immutable licensed inputs into a fresh, private container worker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import time
import re
import xml.etree.ElementTree as ET

from .headless import prepare, prepare_rendered
from .virtual_display import DisplaySettings, run_display


def stage(game, mods, profile, gabs, root, game_root=None, unity_gc_time_slice=None, display=None, cache_key=None):
    game, mods, profile, gabs, root = [Path(p).resolve() for p in (game, mods, profile, gabs, root)]
    if unity_gc_time_slice not in (None, 0):
        raise ValueError('Unity GC time slice supports only source (None) or zero')
    private_game = Path(game_root).resolve() if game_root is not None else root/'game'
    if private_game.exists() or private_game == root or private_game in root.parents:
        raise ValueError('Private game directory must be fresh and separate from the worker root')
    for source in (game, mods, profile, gabs):
        if (source == root or source in root.parents or root in source.parents
                or source == private_game or source in private_game.parents or private_game in source.parents):
            raise ValueError('Worker output must be separate from every input')
    required = [game/'RimWorldLinux', gabs, profile/'Config/Prefs.xml',
                profile/'Config/ModsConfig.xml', profile/'Saves/RimBot-tribal8-baseline.rws',
                mods/'RimBridgeServer/About/About.xml', mods/'RimBotObservations/About/About.xml']
    if display is None:
        required.append(mods/'RimBotHeadless/Assemblies/HeadlessRimPatch.dll')
    boot_config = game/'RimWorldLinux_Data/boot.config'
    if unity_gc_time_slice is not None:
        required.append(boot_config)
    for path in required:
        if not path.is_file():
            raise ValueError(f'Missing Linux worker input: {path}')
    original_boot = boot_config.read_bytes() if boot_config.is_file() else None
    if unity_gc_time_slice is not None and not re.search(rb'^gc-max-time-slice=\d+\r?$', original_boot, re.M):
        raise ValueError('Expected an existing Unity gc-max-time-slice setting')
    root.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    print(f'Staging private game files in {private_game}', flush=True)
    # Dereference input links so running workers cannot observe later DLL changes.
    shutil.copytree(game, private_game, ignore=shutil.ignore_patterns('Mods'))
    if unity_gc_time_slice is not None:
        prepared_boot = re.sub(rb'^gc-max-time-slice=\d+', b'gc-max-time-slice=0', original_boot, flags=re.M)
        (private_game/'RimWorldLinux_Data/boot.config').write_bytes(prepared_boot)
    print('Staging private mod DLLs and profile', flush=True)
    shutil.copytree(mods, private_game/'Mods')
    (root/'profile/Config').mkdir(parents=True)
    (root/'profile/Saves').mkdir()
    for relative in ('Config/Prefs.xml', 'Config/ModsConfig.xml', 'Saves/RimBot-tribal8-baseline.rws'):
        shutil.copy2(profile/relative, root/'profile'/relative)
    (root/'gabs').mkdir()
    shutil.copy2(gabs, root/'gabs/gabs')
    for executable in (private_game/'RimWorldLinux', root/'gabs/gabs'):
        executable.chmod(executable.stat().st_mode | 0o111)
    config = {'version': '1.0', 'rimbot': {'gabsExecutable': 'gabs/gabs'}, 'games': {
        'rimbot-trial': {'id': 'rimbot-trial', 'name': 'RimBot container worker',
                        'launchMode': 'DirectPath', 'target': str(private_game/'RimWorldLinux'),
                        'workingDir': str(private_game), 'args': []}}}
    (root/'config').mkdir()
    (root/'config/config.json').write_text(json.dumps(config, indent=2), encoding='utf8')
    if display is None:
        prepare(root)
    else:
        prepare_rendered(root)
        prefs_path = root/'profile/Config/Prefs.xml'
        prefs = ET.parse(prefs_path)
        for key, value in (('screenWidth', display.width), ('screenHeight', display.height),
                           ('fullscreen', 'False'), ('uiScale', 1)):
            node = prefs.getroot().find(key)
            if node is None:
                node = ET.SubElement(prefs.getroot(), key)
            node.text = str(value)
        prefs.write(prefs_path, encoding='utf8', xml_declaration=True)
        config_path = root/'config/config.json'
        rendered = json.loads(config_path.read_text(encoding='utf8'))
        arguments = rendered['games']['rimbot-trial']['args']
        arguments[arguments.index('-screen-width')+1] = str(display.width)
        arguments[arguments.index('-screen-height')+1] = str(display.height)
        arguments.append('-force-glcore')
        config_path.write_text(json.dumps(rendered, indent=2), encoding='utf8')
    files = [root/'gabs/gabs', private_game/'RimWorldLinux', *sorted((private_game/'Mods').rglob('*.dll')),
             root/'profile/Saves/RimBot-tribal8-baseline.rws', *sorted((root/'profile/Config').glob('*.xml'))]
    for relative in ('UnityPlayer.so', 'RimWorldLinux_Data/boot.config',
                     'RimWorldLinux_Data/Managed/Assembly-CSharp.dll',
                     'RimWorldLinux_Data/MonoBleedingEdge/x86_64/libmonobdwgc-2.0.so'):
        if (private_game/relative).is_file():
            files.append(private_game/relative)
    hashes = {}
    for path in files:
        with path.open('rb') as stream:
            relative = Path('game')/path.relative_to(private_game) if path.is_relative_to(private_game) else path.relative_to(root)
            hashes[relative.as_posix()] = hashlib.file_digest(stream, 'sha256').hexdigest()
    (root/'inputs.json').write_text(json.dumps(hashes, indent=2), encoding='utf8')
    elapsed = round(time.monotonic()-started, 3)
    (root/'staging.json').write_text(json.dumps({'elapsed_seconds': elapsed,
        'game_source': str(game), 'mods_source': str(mods), 'profile_source': str(profile),
        'gabs_source': str(gabs), 'private_game': str(private_game),
        'input_cache_key': cache_key,
        'display': display.manifest() if display else None,
        'unity_gc_time_slice': unity_gc_time_slice,
        'source_boot_sha256': hashlib.sha256(original_boot).hexdigest() if original_boot is not None else None}, indent=2), encoding='utf8')
    print(f'Worker inputs ready in {elapsed}s; starting command', flush=True)
    return root


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', default='/inputs/game')
    parser.add_argument('--mods', default='/inputs/mods')
    parser.add_argument('--profile', default='/inputs/profile')
    parser.add_argument('--gabs', default='/inputs/gabs/gabs')
    parser.add_argument('--root', default='/worker/run')
    parser.add_argument('--game-root', default='/opt/rimbot-game', help='Fresh container-local directory for the private game snapshot')
    parser.add_argument('--unity-gc-time-slice', choices=['source', '0'],
                        default=os.environ.get('RIMBOT_UNITY_GC_TIME_SLICE', 'source'),
                        help='Preserve source boot.config, or use the tested zero-time-slice startup mitigation')
    parser.add_argument('--display', choices=['headless', 'xvfb'], default=os.environ.get('RIMBOT_DISPLAY', 'headless'))
    parser.add_argument('--resolution', default=os.environ.get('RIMBOT_DISPLAY_RESOLUTION', '1280x720'))
    parser.add_argument('--renderer', choices=['llvmpipe', 'd3d12'], default=os.environ.get('RIMBOT_DISPLAY_RENDERER', 'llvmpipe'))
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.unity_gc_time_slice not in ('source', '0'):
        parser.error('RIMBOT_UNITY_GC_TIME_SLICE must be source or 0')
    if args.display not in ('headless', 'xvfb') or args.renderer not in ('llvmpipe', 'd3d12'):
        parser.error('Unsupported display or renderer')
    display = DisplaySettings.parse(args.resolution, args.renderer) if args.display == 'xvfb' else None
    cache_root = os.environ.get('RIMBOT_INPUT_CACHE_ROOT')
    cache_key = os.environ.get('RIMBOT_INPUT_CACHE_KEY')
    if bool(cache_root) != bool(cache_key):
        parser.error('Input cache requires both RIMBOT_INPUT_CACHE_ROOT and RIMBOT_INPUT_CACHE_KEY')
    if cache_root:
        from .container_input_cache import read_manifest
        # The host helper verifies all payload bytes before mounting this snapshot read-only.
        read_manifest(cache_root, cache_key)
        args.game, args.mods, args.gabs = [Path(cache_root)/p for p in ('game', 'mods', 'gabs/gabs')]
    root = stage(args.game, args.mods, args.profile, args.gabs, args.root, args.game_root,
                 0 if args.unity_gc_time_slice == '0' else None, display, cache_key)
    os.environ.update(RIMBOT_BRIDGE_ROOT=str(root), RIMBOT_DATA=str(root/'data'),
                      RIMBOT_HEADLESS='0' if display else '1', RIMBOT_BRIDGE_FRESH='1')
    command = args.command or ['python', '-m', 'rimbot', '--host', '0.0.0.0']
    if command[0] == '--':
        command = command[1:]
    if not command:
        parser.error('Command cannot be empty')
    if display:
        raise SystemExit(run_display(root, display, command))
    os.execvp(command[0], command)


if __name__ == '__main__':
    main()

"""Prepare licensed game/mod/GABS inputs once in a content-addressed Docker volume."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'controller'))
from rimgovernor.container_input_cache import digest, inventory, write_archive
from container_checks import docker_environment


def prepare_cache(game, mods, gabs, image, output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    docker, environment = docker_environment()
    began = time.monotonic()
    manifest, sources = inventory(game, mods, gabs)
    hashed = time.monotonic()
    key = digest(manifest)
    volume = 'rimgovernor-inputs-v1-'+key
    report = {'key': key, 'volume': volume, 'passed': False,
              'hash_seconds': round(hashed-began, 3)}
    (output/'cache-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf8')

    def command(*args, **kwargs):
        return subprocess.run([docker, *args], env=environment, **kwargs)

    def operate(operation, stream=None):
        name = 'rimgovernor-cache-'+uuid.uuid4().hex[:12]
        try:
            with (output/f'cache-{operation}.log').open('w', encoding='utf8') as log:
                return command('run', '--rm', '--network', 'none', '-i', '--name', name,
                    '--mount', f'type=volume,source={volume},target=/cache'+(',readonly' if operation == 'verify' else ''),
                    '--entrypoint', 'python', image, '-m', 'rimgovernor.container_input_cache',
                    operation, '--key', key, stdin=stream or subprocess.DEVNULL,
                    stdout=log, stderr=subprocess.STDOUT, timeout=900)
        finally:
            # Only this helper's container; shared cache volumes survive cleanup.
            result = command('rm', '-f', name, capture_output=True, text=True, timeout=30)
            if result.returncode and 'No such container' not in result.stderr:
                raise RuntimeError(f'Cache helper cleanup failed: {result.stderr}')

    try:
        command('volume', 'create', '--label', 'rimgovernor.kind=input-cache',
                '--label', 'rimgovernor.input-sha256='+key, volume, check=True, capture_output=True, timeout=30)
        result = operate('verify')
        report['hit'] = result.returncode == 0
        if result.returncode == 3:
            with tempfile.TemporaryFile() as archive:
                archive.write(json.dumps(manifest).encode()+b'\n')
                write_archive(archive, manifest, sources)
                archive.seek(0)
                operate('populate', archive).check_returncode()
        else:
            result.check_returncode()
        report['passed'] = True
        return report
    finally:
        report['elapsed_seconds'] = round(time.monotonic()-began, 3)
        (output/'cache.json').write_text(json.dumps(report, indent=2), encoding='utf8')


def compose_override(image, cache=None):
    override = {'services': {'worker': {'image': image}}}
    if cache:
        override['services']['worker'].update(
            environment={'RIMGOVERNOR_INPUT_CACHE_ROOT': '/cached-inputs/snapshot', 'RIMGOVERNOR_INPUT_CACHE_KEY': cache['key']},
            volumes=[{'type': 'volume', 'source': 'input-cache', 'target': '/cached-inputs', 'read_only': True}])
        override['volumes'] = {'input-cache': {'external': True, 'name': cache['volume']}}
    return override


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('game', 'mods', 'gabs', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--image', required=True, help='Built worker image containing the cache helper')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    cache = prepare_cache(args.game, args.mods, args.gabs/'gabs', args.image, args.output)
    (args.output/'cache-compose.json').write_text(json.dumps(compose_override(args.image, cache), indent=2), encoding='utf8')
    print(json.dumps(cache, indent=2))

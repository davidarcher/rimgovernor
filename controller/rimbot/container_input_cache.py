"""Content-addressed, read-only Docker input snapshots; never cache worker state."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tarfile
import uuid


def digest(manifest):
    return hashlib.sha256(json.dumps(manifest, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def inventory(game, mods, gabs):
    """Hash actual bytes, dereferencing links and refusing directory cycles."""
    entries, sources = {}, {}

    def visit(path, name, parents=(), skip_mods=False):
        resolved = path.resolve(strict=True)
        if resolved in parents:
            raise ValueError(f'Input directory cycle: {path}')
        if path.is_dir():
            entries[name] = {'type': 'directory'}
            for child in sorted(path.iterdir()):
                if not (skip_mods and child.name == 'Mods'):
                    visit(child, name+'/'+child.name, (*parents, resolved), skip_mods)
        elif path.is_file():
            with path.open('rb') as stream:
                sha = hashlib.file_digest(stream, 'sha256').hexdigest()
            # Windows bind mounts expose executable inputs without POSIX mode metadata.
            executable = os.name == 'nt' or bool(path.stat().st_mode & 0o111)
            entries[name] = {'type': 'file', 'sha256': sha, 'size': path.stat().st_size,
                             'mode': 0o755 if executable else 0o644}
            sources[name] = path
        else:
            raise ValueError(f'Unsupported input: {path}')

    visit(Path(game), 'game', skip_mods=True)
    visit(Path(mods), 'mods')
    visit(Path(gabs), 'gabs/gabs')
    entries['gabs'] = {'type': 'directory'}
    return {'version': 1, 'entries': entries}, sources


def write_archive(stream, manifest, sources):
    """A plain-file archive avoids carrying links into a shared snapshot."""
    with tarfile.open(fileobj=stream, mode='w|') as archive:
        for name, entry in sorted(manifest['entries'].items()):
            member = tarfile.TarInfo(name)
            if entry['type'] == 'directory':
                member.type, member.mode = tarfile.DIRTYPE, 0o755
                archive.addfile(member)
            else:
                member.size, member.mode = entry['size'], entry['mode']
                with sources[name].open('rb') as source:
                    archive.addfile(member, source)


def read_manifest(snapshot, key):
    manifest = json.loads((Path(snapshot)/'manifest.json').read_text(encoding='utf8'))
    if manifest.get('version') != 1 or digest(manifest) != key:
        raise ValueError('Input cache manifest does not match its content key')
    return manifest


def verify(snapshot, key):
    snapshot = Path(snapshot)
    manifest = read_manifest(snapshot, key)
    for name, entry in manifest['entries'].items():
        relative = PurePosixPath(name)
        if relative.is_absolute() or '..' in relative.parts or '\\' in name:
            raise ValueError(f'Invalid cache path: {name}')
        path = snapshot/name
        if path.is_symlink() or not path.resolve().is_relative_to(snapshot.resolve()):
            raise ValueError(f'Cache must contain private regular files: {name}')
        if entry['type'] == 'directory':
            if not path.is_dir():
                raise ValueError(f'Missing cache directory: {name}')
        else:
            with path.open('rb') as stream:
                sha = hashlib.file_digest(stream, 'sha256').hexdigest()
            if path.stat().st_size != entry['size'] or sha != entry['sha256']:
                raise ValueError(f'Corrupt cache input: {name}')
            if os.name == 'posix' and path.stat().st_mode & 0o777 != entry['mode']:
                raise ValueError(f'Corrupt cache permissions: {name}')
    actual = {p.relative_to(snapshot).as_posix() for p in snapshot.rglob('*')}
    if actual != set(manifest['entries']) | {'manifest.json'}:
        raise ValueError('Unexpected files in input cache')
    return manifest


def populate(cache, manifest, stream):
    """Publish only a fully verified snapshot; concurrent writers serialize on Linux."""
    import fcntl
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    key = digest(manifest)
    with (cache/'.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        snapshot = cache/'snapshot'
        if snapshot.exists():
            verify(snapshot, key)
            return True
        temporary = cache/('incomplete-'+uuid.uuid4().hex)
        temporary.mkdir()
        try:
            seen = set()
            with tarfile.open(fileobj=stream, mode='r|') as archive:
                for member in archive:
                    name = member.name.rstrip('/')
                    relative = PurePosixPath(name)
                    entry = manifest['entries'].get(name)
                    if (entry is None or name in seen or relative.is_absolute()
                            or '..' in relative.parts or '\\' in name):
                        raise ValueError(f'Unexpected archive path: {name}')
                    seen.add(name)
                    destination = temporary/name
                    if member.isdir() and entry['type'] == 'directory':
                        destination.mkdir(parents=True, exist_ok=True)
                    elif member.isfile() and entry['type'] == 'file' and member.size == entry['size']:
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        with archive.extractfile(member) as source, destination.open('xb') as target:
                            shutil.copyfileobj(source, target)
                        if entry['mode'] not in (0o644, 0o755):
                            raise ValueError(f'Invalid cache permissions: {name}')
                        destination.chmod(entry['mode'])
                    else:
                        raise ValueError(f'Invalid archive member: {name}')
            if seen != set(manifest['entries']):
                raise ValueError('Incomplete input archive')
            (temporary/'manifest.json').write_text(json.dumps(manifest), encoding='utf8')
            verify(temporary, key)
            temporary.rename(snapshot)
        except BaseException:
            # Retain incomplete uploads for diagnosis; they are never cache hits.
            raise
    return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['verify', 'populate'])
    parser.add_argument('--cache', default='/cache')
    parser.add_argument('--key', required=True)
    args = parser.parse_args()
    snapshot = Path(args.cache)/'snapshot'
    if args.operation == 'verify':
        if not snapshot.exists():
            raise SystemExit(3)
        verify(snapshot, args.key)
        hit = True
    else:
        manifest = json.loads(sys.stdin.buffer.readline())
        if digest(manifest) != args.key:
            raise ValueError('Upload manifest does not match requested key')
        hit = populate(args.cache, manifest, sys.stdin.buffer)
    print(json.dumps({'key': args.key, 'hit': hit}))


if __name__ == '__main__':
    main()

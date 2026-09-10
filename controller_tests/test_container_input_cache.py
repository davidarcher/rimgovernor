import io
import json
import os
import tarfile
import importlib.util
from pathlib import Path
import subprocess
from concurrent.futures import ThreadPoolExecutor

import pytest

from rimgovernor.container_input_cache import digest, inventory, populate, verify, write_archive
from rimgovernor.container_worker import stage
from test_container_worker import inputs


def bundle(sources):
    game, mods, _, gabs = sources
    manifest, paths = inventory(game, mods, gabs)
    archive = io.BytesIO()
    write_archive(archive, manifest, paths)
    archive.seek(0)
    return manifest, archive


def test_content_key_detects_same_size_same_mtime_and_removed_inputs(tmp_path):
    sources = inputs(tmp_path)
    file = sources[1]/'RimGovernorHeadless/Assemblies/HeadlessRimPatch.dll'
    before = file.stat()
    original, _ = bundle(sources)
    file.write_bytes(b'other')
    os.utime(file, ns=(before.st_atime_ns, before.st_mtime_ns))
    changed, _ = bundle(sources)
    assert digest(original) != digest(changed)
    file.unlink()
    removed, _ = bundle(sources)
    assert digest(removed) != digest(changed)
    (sources[2]/'Saves/RimGovernor-tribal8-baseline.rws').write_bytes(b'new save')
    assert digest(bundle(sources)[0]) == digest(removed)


linux = pytest.mark.skipif(os.name != 'posix', reason='Docker cache publication uses Linux flock')


@linux
def test_cached_workers_remain_private_and_profile_stays_fresh(tmp_path):
    sources = inputs(tmp_path)
    helper = sources[0]/'UnityCrashHandler64'
    helper.write_bytes(b'helper executable')
    helper.chmod(0o755)
    manifest, archive = bundle(sources)
    cache = tmp_path/'cache'
    assert populate(cache, manifest, archive) is False
    assert populate(cache, manifest, io.BytesIO()) is True
    snapshot = cache/'snapshot'
    verify(snapshot, digest(manifest))
    assert (snapshot/'game/UnityCrashHandler64').stat().st_mode & 0o111
    roots = []
    for name in ('a', 'b'):
        roots.append(stage(snapshot/'game', snapshot/'mods', sources[2], snapshot/'gabs/gabs',
                           tmp_path/name, cache_key=digest(manifest)))
    dll = 'Mods/RimGovernorHeadless/Assemblies/HeadlessRimPatch.dll'
    (roots[0]/'game'/dll).write_bytes(b'worker mutation')
    (sources[1]/'RimGovernorHeadless/Assemblies/HeadlessRimPatch.dll').write_bytes(b'new build')
    assert (roots[1]/'game'/dll).read_bytes() == b'first'
    verify(snapshot, digest(manifest))
    (sources[2]/'Saves/RimGovernor-tribal8-baseline.rws').write_bytes(b'new save')
    third = stage(snapshot/'game', snapshot/'mods', sources[2], snapshot/'gabs/gabs', tmp_path/'c')
    assert (third/'profile/Saves/RimGovernor-tribal8-baseline.rws').read_bytes() == b'new save'
    assert json.loads((roots[0]/'staging.json').read_text())['input_cache_key'] == digest(manifest)


@linux
def test_concurrent_publication_and_corruption_refusal(tmp_path):
    sources = inputs(tmp_path)
    manifest, archive = bundle(sources)
    payload = archive.getvalue()
    cache = tmp_path/'cache'
    with ThreadPoolExecutor(2) as pool:
        results = list(pool.map(lambda _: populate(cache, manifest, io.BytesIO(payload)), range(2)))
    assert sorted(results) == [False, True]
    (cache/'snapshot/game/RimWorldLinux').write_bytes(b'bad')
    with pytest.raises(ValueError, match='Corrupt'):
        populate(cache, manifest, io.BytesIO(payload))


@linux
def test_changed_source_during_upload_never_publishes(tmp_path):
    sources = inputs(tmp_path)
    manifest, paths = inventory(sources[0], sources[1], sources[3])
    sources[3].write_bytes(b'xxxx')
    archive = io.BytesIO()
    write_archive(archive, manifest, paths)
    archive.seek(0)
    cache = tmp_path/'cache'
    with pytest.raises(ValueError, match='Corrupt'):
        populate(cache, manifest, archive)
    assert not (cache/'snapshot').exists()
    assert list(cache.glob('incomplete-*'))


@linux
@pytest.mark.parametrize('name,kind', [('../escape', tarfile.REGTYPE), ('game/link', tarfile.SYMTYPE)])
def test_archive_traversal_and_links_are_refused(tmp_path, name, kind):
    manifest, _ = bundle(inputs(tmp_path))
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode='w') as tar:
        entry = tarfile.TarInfo(name)
        entry.type = kind
        entry.linkname = '/tmp/escape'
        tar.addfile(entry)
    archive.seek(0)
    with pytest.raises(ValueError, match='archive'):
        populate(tmp_path/'cache', manifest, archive)
    assert not (tmp_path/'cache/snapshot').exists()


@linux
def test_linked_inputs_are_dereferenced_and_cycles_rejected(tmp_path):
    sources = inputs(tmp_path)
    linked = sources[1]/'linked.dll'
    linked.symlink_to(sources[3])
    manifest, archive = bundle(sources)
    cache = tmp_path/'cache'
    populate(cache, manifest, archive)
    assert (cache/'snapshot/mods/linked.dll').read_bytes() == b'gabs'
    assert not (cache/'snapshot/mods/linked.dll').is_symlink()
    (sources[1]/'loop').symlink_to(sources[1], target_is_directory=True)
    with pytest.raises(ValueError, match='cycle'):
        bundle(sources)


@pytest.mark.parametrize('state', ['hit', 'miss', 'corrupt', 'timeout'])
def test_host_cache_hit_skips_upload_and_failures_keep_evidence(tmp_path, monkeypatch, state):
    scripts = Path(__file__).resolve().parents[1]/'scripts'
    monkeypatch.syspath_prepend(str(scripts))
    spec = importlib.util.spec_from_file_location('cache_host', scripts/'container_input_cache.py')
    host = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(host)
    monkeypatch.setattr(host, 'docker_environment', lambda: ('docker', {}))
    calls = []

    def command(args, **kwargs):
        calls.append(args)
        if args[1] == 'run':
            if state == 'timeout':
                raise subprocess.TimeoutExpired(args, 900)
            operation = args[args.index('rimgovernor.container_input_cache')+1]
            code = (3 if state == 'miss' else 1 if state == 'corrupt' else 0) if operation == 'verify' else 0
            return subprocess.CompletedProcess(args, code)
        return subprocess.CompletedProcess(args, 0, '', '')

    monkeypatch.setattr(host.subprocess, 'run', command)
    sources = inputs(tmp_path)
    output = tmp_path/'evidence'
    if state in ('corrupt', 'timeout'):
        with pytest.raises((subprocess.CalledProcessError, subprocess.TimeoutExpired)):
            host.prepare_cache(sources[0], sources[1], sources[3], 'image', output)
        assert not json.loads((output/'cache.json').read_text())['passed']
    else:
        result = host.prepare_cache(sources[0], sources[1], sources[3], 'image', output)
        assert result['hit'] == (state == 'hit')
        override = host.compose_override('image', result)
        assert override['services']['worker']['volumes'][0]['read_only']
        assert override['volumes']['input-cache']['external']
    runs = [call for call in calls if call[1] == 'run']
    assert len(runs) == (2 if state == 'miss' else 1)
    assert len([call for call in calls if call[1] == 'rm']) == len(runs)

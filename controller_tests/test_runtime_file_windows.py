"""Real filesystem fault fixtures; no GABS/game dependency."""
import importlib.util
import os
from pathlib import Path

import pytest


pytestmark = pytest.mark.skipif(os.name != 'nt', reason='Windows sharing semantics')


def fixture_module():
    path = Path(__file__).resolve().parents[1]/'scripts/runtime_file_acceptance.py'
    spec = importlib.util.spec_from_file_location('runtime_file_acceptance', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize('share', [3, 7])
def test_open_runtime_target_blocks_replace_until_reader_closes(tmp_path, share):
    module = fixture_module()
    runtime, pending = tmp_path/'runtime.json', tmp_path/'.runtime.tmp'
    runtime.write_text('old')
    pending.write_text('new')
    with module.runtime_handle(runtime, share):
        assert runtime.read_text() == 'old'
        with pytest.raises(OSError):
            os.replace(pending, runtime)
        assert runtime.read_text() == 'old'
    os.replace(pending, runtime)
    assert runtime.read_text() == 'new'


def test_exclusive_runtime_reader_releases_handle_even_on_exception(tmp_path):
    module = fixture_module()
    runtime = tmp_path/'runtime.json'
    runtime.write_text('old')
    with pytest.raises(ValueError, match='fixture'):
        with module.runtime_handle(runtime, 0):
            with pytest.raises(OSError):
                runtime.read_text()
            raise ValueError('fixture')
    assert runtime.read_text() == 'old'

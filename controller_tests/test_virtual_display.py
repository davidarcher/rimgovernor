import json
from types import SimpleNamespace
import pytest
from rimbot import virtual_display as display


class Process:
    def __init__(self, code=None):
        self.returncode = code
        self.terminated = False

    def poll(self):
        return self.returncode

    def terminate(self):
        self.terminated = True
        self.returncode = 0

    def wait(self, timeout=None):
        return self.returncode


@pytest.mark.parametrize('renderer_ok', [True, False])
@pytest.mark.parametrize('renderer_name', ['llvmpipe', 'd3d12'])
@pytest.mark.parametrize('child_code, expected', [(7, 7), (-15, 143)])
def test_display_verifies_renderer_and_cleans_up(tmp_path, monkeypatch, renderer_ok, renderer_name, child_code, expected):
    server, child = Process(), Process(child_code)
    launched = []
    def launch(command, **kwargs):
        launched.append((command, kwargs))
        return server if command[0] == 'Xvfb' else child
    def run(command, **kwargs):
        output = (renderer_name + '\nAccelerated: yes').encode() if renderer_ok else b'unknown renderer'
        return SimpleNamespace(returncode=0, stdout=output, stderr=b'')
    monkeypatch.setattr(display.subprocess, 'Popen', launch)
    monkeypatch.setattr(display.subprocess, 'run', run)
    if renderer_ok:
        assert display.run_display(tmp_path, display.DisplaySettings.parse('1280x720', renderer_name), ['worker']) == expected
        assert launched[1][1]['env']['LIBGL_ALWAYS_SOFTWARE'] == '1'
    else:
        with pytest.raises(RuntimeError, match=renderer_name):
            display.run_display(tmp_path, display.DisplaySettings.parse('1280x720', renderer_name), ['worker'])
        assert len(launched) == 1
    assert server.terminated
    assert '-nolisten' in launched[0][0]
    assert (tmp_path/'display/renderer.log').is_file()
    result = json.loads((tmp_path/'display/result.json').read_text())
    assert ('error' in result) != renderer_ok


def test_display_death_stops_owned_command(tmp_path, monkeypatch):
    server, child = Process(), Process()
    def launch(command, **kwargs):
        if command[0] == 'Xvfb':
            return server
        server.returncode = 1
        return child
    monkeypatch.setattr(display.subprocess, 'Popen', launch)
    monkeypatch.setattr(display.subprocess, 'run', lambda *a, **kw: SimpleNamespace(returncode=0, stdout=b'llvmpipe', stderr=b''))
    with pytest.raises(RuntimeError, match='while the worker'):
        display.run_display(tmp_path, display.DisplaySettings.parse('1280x720'), ['worker'])
    assert child.terminated


@pytest.mark.asyncio
async def test_bridge_passes_display_settings_without_unrelated_environment(tmp_path, monkeypatch):
    from contextlib import asynccontextmanager
    from rimbot import bridge
    monkeypatch.setenv('DISPLAY', ':99')
    monkeypatch.setenv('GALLIUM_DRIVER', 'llvmpipe')
    monkeypatch.setenv('UNRELATED_SECRET', 'excluded')
    @asynccontextmanager
    async def transport(parameters):
        assert parameters.env['DISPLAY'] == ':99'
        assert parameters.env['GALLIUM_DRIVER'] == 'llvmpipe'
        assert 'UNRELATED_SECRET' not in parameters.env
        raise RuntimeError('transport inspected')
        yield
    monkeypatch.setattr(bridge, 'stdio_client', transport)
    with pytest.raises(RuntimeError, match='transport inspected'):
        async with bridge.bridge_session(tmp_path/'gabs', tmp_path):
            pass

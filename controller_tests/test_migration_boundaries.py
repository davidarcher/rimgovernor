"""Run the migration orchestrator through every interrupted ownership boundary."""
from contextlib import asynccontextmanager, contextmanager
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from test_legacy_migration import migration, snapshot
from rimgovernor.store import Store


PHASES = ['controller_suspended', 'database_copied', 'takeover_pending', 'taken_over',
          'save_pending', 'saved', 'game_stop_pending', 'game_stopped',
          'controller_stopped', 'replacement_started']


@pytest.mark.asyncio
@pytest.mark.parametrize('phase', PHASES)
async def test_interrupted_migration_never_replays_ownership_save_or_stop(tmp_path, monkeypatch, phase):
    saved, state = snapshot()
    state.update(sessionId='colony:0:old', connected=True, mode='manual',
                 game={'paused': True, 'tick': 100}, headless=False, chatModel='local')
    source = tmp_path/'source'; source.mkdir()
    root = tmp_path/'root'; root.mkdir()
    configuration = root/'config'; configuration.mkdir()
    (configuration/'config.json').write_text(json.dumps({'rimgovernor':{'gabsExecutable':'gabs/test-gabs'}}))
    database = tmp_path/'original.sqlite'
    store = Store(database); store.set('bridge:colony:0', saved); store.close()
    calls = []
    class Handle:
        def __init__(self, pid, birth=None): self.pid, self.birth, self.dead = pid, birth or 123, False
        def alive(self): return not self.dead
        def close(self): pass
        def terminate(self): self.dead=True; calls.append('controller_stop')
        @contextmanager
        def paused(self, marker):
            try: yield
            finally:
                if not self.dead: calls.append('resume_original')
    class Client:
        async def __aenter__(self): return self
        async def __aexit__(self, *_): pass
        async def get(self, path):
            value = {'pid': 101, 'source_root': str(source), 'backend': 'rimbridge'} if path == '/api/health' else state
            return SimpleNamespace(json=lambda: value)
        async def post(self, *_, **__): return SimpleNamespace(raise_for_status=lambda: None)
    async def core(name, **_):
        if name == 'games_connect': calls.append('takeover')
        return SimpleNamespace(structuredContent={'diagnostics': {'runtime': {
            'gamePid': 202, 'pidStartTime': 456, 'launchMode': 'DirectPath', 'pidRole': 'workload'}}})
    @asynccontextmanager
    async def session(*_):
        yield SimpleNamespace(core=core, game_id='rimgovernor-trial')
    async def checkpoint(*_):
        calls.append('save')
        return {'manifest_path': str(root/'checkpoint.json'), 'tick': 100}
    async def stop(*_): calls.append('game_stop')
    original_boundary = migration.boundary
    def crash(work, report, current):
        original_boundary(work, report, current)
        if current == phase: raise RuntimeError('injected interruption: '+phase)
    monkeypatch.setattr(migration, 'ProcessHandle', Handle)
    monkeypatch.setattr(migration, 'read_game_claim', lambda config, runtime: runtime)
    monkeypatch.setattr(migration.httpx, 'AsyncClient', lambda **_: Client())
    monkeypatch.setattr(migration, 'profile_path', lambda *_: root)
    monkeypatch.setattr(migration, 'bridge_session', session)
    monkeypatch.setattr(migration, 'BridgeGame', lambda _: None)
    monkeypatch.setattr(migration, 'BridgeRuntime', lambda *_, **__: SimpleNamespace(
        sync_identity=AsyncMock(), context_token=state['sessionId'],
        public=lambda: state, router=SimpleNamespace(close=AsyncMock())))
    monkeypatch.setattr(migration, 'observe', AsyncMock(return_value=SimpleNamespace(summary=SimpleNamespace(end_tick=100))))
    monkeypatch.setattr(migration, 'create_checkpoint', checkpoint)
    monkeypatch.setattr(migration, 'stop_for_restart', stop)
    monkeypatch.setattr(migration, 'boundary', crash)
    monkeypatch.setattr(migration.subprocess, 'Popen', lambda *_, **__: calls.append('replacement'))
    monkeypatch.setattr(migration.subprocess, 'CREATE_NO_WINDOW', 0, raising=False)
    monkeypatch.setattr(migration.subprocess, 'CREATE_NEW_PROCESS_GROUP', 0, raising=False)
    args = SimpleNamespace(root=root, source=source, database=database, port=8787, recover_disconnected=False)
    with pytest.raises(RuntimeError, match='injected interruption'):
        await migration.migrate(args)
    stage = PHASES.index(phase)
    for call, after in [('takeover', 3), ('save', 5), ('game_stop', 7), ('controller_stop', 8), ('replacement', 9)]:
        assert calls.count(call) == int(stage >= after), (phase, calls)
    report = json.loads(next((root/'migrations').glob('*/report.json')).read_text())
    assert report['phase'] == phase and report['outcome'] == 'INTERRUPTED'
    assert calls.count('resume_original') == int(stage < 8)
    store = Store(database)
    assert store.get('bridge:colony:0') == saved
    store.close()

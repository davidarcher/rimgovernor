import asyncio
from contextlib import asynccontextmanager
import json
from types import SimpleNamespace

import pytest

from rimgovernor import native_trials as trials
from rimgovernor.container_worker import stage
from test_container_worker import inputs


class Result:
    def __init__(self, **values):
        self.structuredContent = values

    def model_dump(self, **_):
        return self.structuredContent


class Bridge:
    game_id = 'rimgovernor-trial'

    def __init__(self):
        self.calls, self.load, self.tick = [], 0, 10
        self.paused, self.active = True, False
        self.bad_load = False

    async def core(self, name, **args):
        self.calls.append(name)
        return Result(success=True)

    async def connect(self):
        pass

    async def call(self, name, **args):
        self.calls.append(name)
        if name == 'rimworld/load_game_ready':
            if not self.bad_load:
                self.load += 1
        if name == 'home/colony_identity':
            return Result(colonyId='colony', mapId=1, loadToken=str(self.load))
        if name == 'home/status':
            return Result(time={'paused': self.paused, 'ticksGame': self.tick})
        if name == 'home/supervised_play':
            return Result(active=self.active)
        if name == 'slow':
            await asyncio.Event().wait()
        return Result(success=True)


class Runtime:
    def __init__(self, store, root, **kwargs):
        self.store = store
        self.review_task = self.execution_task = self.clock_task = None
        self.draft_owners = {}
        self.router = SimpleNamespace(close=self.close)
        self.mode = 'manual'

    async def close(self):
        pass

    async def sync_identity(self):
        self.identity = (await self.bridge.call('home/colony_identity')).structuredContent

    async def halt(self):
        self.draft_owners.clear()


@pytest.fixture
def fixture(tmp_path, monkeypatch):
    sources = inputs(tmp_path)
    (sources[2]/'Saves/RimGovernor-tribal8-baseline.rws').write_bytes(b'<tickManager><ticksGame>10</ticksGame></tickManager>')
    source = stage(*sources, tmp_path/'source')
    bridge = Bridge()

    @asynccontextmanager
    async def session(*args):
        yield bridge

    async def observe(game):
        return SimpleNamespace()

    monkeypatch.setattr(trials, 'observe', observe)
    return trials.ReusableGame(source, tmp_path/'reuse', session_factory=session, runtime_factory=Runtime), bridge


async def test_one_process_new_database_new_load_and_revoked_old_client(fixture):
    game, bridge = fixture
    async with game:
        async with game.trial('first') as old:
            old.store.set('marker', 'from first')
            old_token = old.identity['loadToken']
        async with game.trial('second') as new:
            assert new.store.get('marker') is None
            assert new.identity['loadToken'] != old_token
            count = len(bridge.calls)
            with pytest.raises(RuntimeError, match='revoked'):
                await old.bridge.call('write')
            assert len(bridge.calls) == count
    assert bridge.calls.count('games_start') == bridge.calls.count('games_kill') == 1
    assert bridge.calls.count('rimworld/load_game_ready') == 2
    assert all(row['cleanup_verified'] for row in game.report['trials'])
    with pytest.raises(RuntimeError, match='retired'):
        async with game.trial('after_close'):
            pass


@pytest.mark.parametrize('failure', ['case', 'input', 'external_load', 'same_token', 'tick', 'pause', 'lease'])
async def test_unsafe_reuse_is_retired_without_retry(fixture, failure):
    game, bridge = fixture
    async with game:
        async with game.trial('first'):
            pass
        if failure == 'input':
            game.baseline.write_bytes(b'changed baseline')
        elif failure == 'external_load':
            bridge.load += 1
        elif failure == 'same_token':
            bridge.bad_load = True
        elif failure == 'tick':
            bridge.tick = 999
        elif failure == 'pause':
            bridge.paused = False
        elif failure == 'lease':
            bridge.active = True
        with pytest.raises((RuntimeError, AssertionError)):
            async with game.trial('bad'):
                if failure == 'case':
                    raise AssertionError('case failed')
        count = len(bridge.calls)
        with pytest.raises(RuntimeError, match='retired'):
            async with game.trial('not_run'):
                pass
        assert len(bridge.calls) == count
    assert game.poisoned
    assert not game.report['trials'][-1]['passed']
    assert bridge.calls.count('games_kill') == 1


async def test_unfinished_native_request_is_cancelled_and_retires_game(fixture):
    game, bridge = fixture
    async with game:
        with pytest.raises(RuntimeError, match='unfinished'):
            async with game.trial('first') as rt:
                pending = asyncio.create_task(rt.bridge.call('slow'))
                await asyncio.sleep(0)
        assert pending.cancelled()
        assert not rt.bridge.active
        assert game.poisoned


async def test_cases_cannot_overlap(fixture):
    game, _ = fixture
    async with game:
        async with game.trial('first'):
            with pytest.raises(RuntimeError, match='busy'):
                async with game.trial('second'):
                    pass


async def test_router_cleanup_failure_retires_worker_and_closes_store(fixture):
    game, _ = fixture
    async def fail():
        raise RuntimeError('close failed')
    async with game:
        with pytest.raises(RuntimeError, match='close failed'):
            async with game.trial('first') as rt:
                rt.router.close = fail
        assert game.poisoned and not rt.bridge.active
        assert game.report['trials'][0]['passed'] is False
        import sqlite3
        with pytest.raises(sqlite3.ProgrammingError):
            rt.store.get('closed')


@pytest.mark.parametrize('reuse,fail', [(True, False), (True, True), (False, False)])
async def test_execution_suite_reuses_only_when_requested_and_stops_after_failure(tmp_path, monkeypatch, reuse, fail):
    import importlib.util
    from pathlib import Path
    scripts = Path(__file__).resolve().parents[1]/'scripts'
    monkeypatch.syspath_prepend(str(scripts))
    spec = importlib.util.spec_from_file_location('execution_probe', scripts/'execution_acceptance_smoke.py')
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
    launches, cases = [], []

    @asynccontextmanager
    async def game(source, output):
        launches.append(output)
        yield object()

    async def run(args, case, owner):
        cases.append(case)
        return {'case': case, 'outcome': 'error' if fail else 'passed'}

    monkeypatch.setattr(probe, 'ReusableGame', game)
    monkeypatch.setattr(probe, 'run_case', run)
    args = SimpleNamespace(output=tmp_path/'results', source_root=tmp_path, case='all', reuse_game=reuse)
    assert await probe.main(args) is not fail
    assert len(launches) == (1 if reuse else 3)
    assert len(cases) == (1 if fail else 3)
    results = json.loads((args.output/'summary.json').read_text())
    assert len(results) == 3
    if fail:
        assert results[1]['outcome'] == results[2]['outcome'] == 'not_run'

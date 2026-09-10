"""Sequential, explicit native test cases sharing one owned headless game process."""
import asyncio
from contextlib import AsyncExitStack, asynccontextmanager
import json
from pathlib import Path
import re
import sys
import time
import xml.etree.ElementTree as ET

from .bridge import bridge_session, gabs_executable
from .bridge_game import BridgeGame
from .bridge_observation import observe
from .bridge_runtime import BridgeRuntime
from .campaign_manifest import file_hash
from .headless import isolated_root, prepare
from .store import Store


class TrialBridge:
    """Revoke retained clients before the next case can use the shared connection."""
    def __init__(self, bridge):
        self.bridge, self.game_id = bridge, bridge.game_id
        self.active, self.pending = True, set()

    async def _call(self, method, *args, **kwargs):
        if not self.active:
            raise RuntimeError('Native trial has ended; its bridge is revoked')
        task = asyncio.current_task()
        self.pending.add(task)
        try:
            return await getattr(self.bridge, method)(*args, **kwargs)
        finally:
            self.pending.discard(task)

    async def call(self, name, **arguments):
        return await self._call('call', name, **arguments)

    async def detail(self, name):
        return await self._call('detail', name)

    async def names(self, **arguments):
        return await self._call('names', **arguments)


class TrialRuntime(BridgeRuntime):
    async def start(self):
        raise RuntimeError('Native trials use explicit test steps, not the background runtime loop')


class ReusableGame:
    """Fresh database/runtime and verified baseline reload for each sequential case.

    Test-only: a baseline reload does not reset mod static state. Any failed case,
    changed load, changed inputs or unfinished dispatch retires the whole worker.
    """
    def __init__(self, source, output, *, session_factory=None, runtime_factory=TrialRuntime):
        self.source, self.output = Path(source), Path(output)
        self.session_factory = session_factory or bridge_session
        self.runtime_factory = runtime_factory
        self.stack = AsyncExitStack()
        self.bridge = None
        self.busy = self.poisoned = False
        self.last_identity = None
        self.closed = False
        self.report = {'scope': 'Reused headless process, fresh native load and controller database per case', 'trials': []}

    def save_report(self):
        (self.output/'reuse.json').write_text(json.dumps(self.report, indent=2), encoding='utf8')

    def fingerprints(self):
        config = json.loads((self.configuration/'config.json').read_text(encoding='utf8'))['games']['rimgovernor-trial']
        paths = [self.baseline, self.configuration/'config.json', gabs_executable(self.root),
                 self.root/'headless-profile/Config/ModsConfig.xml', Path(config['target']),
                 *sorted(p for p in (Path(config['workingDir'])/'Mods').rglob('*') if p.is_file())]
        return {str(path): file_hash(path) for path in paths}

    async def __aenter__(self):
        self.output.mkdir(parents=True, exist_ok=False)
        try:
            self.root = isolated_root(self.source, self.output/'bridge')
            self.configuration = prepare(self.root)
            self.baseline = self.root/'headless-profile/Saves/RimGovernor-tribal8-baseline.rws'
            ticks = re.findall(rb'<tickManager>\s*<ticksGame>(\d+)</ticksGame>', self.baseline.read_bytes())
            if len(ticks) != 1:
                raise ValueError('Expected one baseline tick-manager value')
            self.tick = int(ticks[0])
            prefs = self.root/'headless-profile/Config/Prefs.xml'
            tree = ET.parse(prefs)
            pause = tree.getroot().find('pauseOnLoad')
            if pause is None:
                pause = ET.SubElement(tree.getroot(), 'pauseOnLoad')
            pause.text = 'True'
            tree.write(prefs, encoding='utf8', xml_declaration=True)
            self.inputs = self.fingerprints()
            self.report.update(inputs=self.inputs, baseline_tick=self.tick)
            began = time.monotonic()
            self.bridge = await self.stack.enter_async_context(self.session_factory(gabs_executable(self.root), self.configuration))
            started = await self.bridge.core('games_start', gameId=self.bridge.game_id)
            self.report['start'] = started.model_dump(mode='json')
            await self.bridge.connect()
            self.report['startup_seconds'] = round(time.monotonic()-began, 3)
            self.save_report()
            return self
        except BaseException:
            await self.__aexit__(*sys.exc_info())
            raise

    async def __aexit__(self, kind, error, traceback):
        try:
            if self.bridge is not None:
                result = await self.bridge.core('games_kill', gameId=self.bridge.game_id)
                self.report['shutdown'] = result.model_dump(mode='json')
        except BaseException as cleanup:
            self.report['cleanup_error'] = repr(cleanup)
            raise
        finally:
            self.closed = True
            if error is not None:
                self.report['error'] = repr(error)
            self.save_report()
            await self.stack.aclose()

    async def identity(self):
        return (await self.bridge.call('home/colony_identity')).structuredContent

    @staticmethod
    def token(identity):
        return tuple(identity[key] for key in ('colonyId', 'mapId', 'loadToken'))

    @asynccontextmanager
    async def trial(self, name, *, settings=None):
        if not re.fullmatch(r'[a-zA-Z0-9_-]+', name):
            raise ValueError('Trial name must be a simple directory name')
        if self.busy or self.poisoned or self.closed or self.bridge is None:
            raise RuntimeError('Reusable game is busy, retired or not started')
        folder = self.output/name
        folder.mkdir(exist_ok=False)
        self.busy = True
        row = {'name': name, 'passed': False}
        self.report['trials'].append(row)
        runtime = store = gate = None
        try:
            if self.fingerprints() != self.inputs:
                raise RuntimeError('Native trial inputs changed; start a new game')
            if self.last_identity and self.token(await self.identity()) != self.token(self.last_identity):
                raise RuntimeError('Native load changed outside the trial; refusing baseline reload')
            began = time.monotonic()
            await self.bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
            identity = await self.identity()
            status = (await self.bridge.call('home/status', colonists=False, threats=False)).structuredContent
            if (status.get('time', {}).get('paused') is not True
                    or status['time']['ticksGame'] not in (self.tick, self.tick+1)):
                raise RuntimeError('Baseline was not loaded paused at its saved tick')
            if self.last_identity and identity['loadToken'] == self.last_identity['loadToken']:
                raise RuntimeError('Baseline reload did not produce a new native load token')
            gate = TrialBridge(self.bridge)
            store = Store(folder/'state.sqlite')
            runtime = self.runtime_factory(store, self.root, headless=True, settings=settings)
            runtime.bridge, runtime.game = gate, BridgeGame(gate)
            await runtime.sync_identity()
            if self.token(runtime.identity) != self.token(identity):
                raise RuntimeError('Native load changed during trial initialization')
            runtime.batch = await observe(runtime.game)
            runtime.connected, runtime.phase = True, 'Manual'
            row.update(identity=identity, tick=status['time']['ticksGame'],
                       ready_seconds=round(time.monotonic()-began, 3))
            self.save_report()
            yield runtime
            row['passed'] = True
        except BaseException as error:
            self.poisoned = True
            row['error'] = repr(error)
            raise
        finally:
            try:
                if runtime is not None:
                    pending = set(gate.pending)
                    pending.update(task for task in (runtime.review_task, runtime.execution_task, runtime.clock_task)
                                   if task is not None and not task.done())
                    if pending:
                        for task in pending:
                            task.cancel()
                        await asyncio.gather(*pending, return_exceptions=True)
                        raise RuntimeError('Trial ended with unfinished tasks; retiring game')
                    if self.token(await self.identity()) != self.token(runtime.identity):
                        raise RuntimeError('Native load changed during trial; refusing cleanup writes')
                    await runtime.halt()
                    status = (await self.bridge.call('home/status', colonists=False, threats=False)).structuredContent
                    clock = (await self.bridge.call('home/supervised_play', op='status')).structuredContent
                    if runtime.draft_owners or not status['time']['paused'] or clock.get('active') is not False:
                        raise RuntimeError('Trial cleanup did not verify pause and released ownership')
                    self.last_identity = await self.identity()
                    if self.token(self.last_identity) != self.token(runtime.identity):
                        raise RuntimeError('Native load changed during trial cleanup')
                    row['cleanup_verified'] = True
            except BaseException as error:
                row.update(passed=False, cleanup_error=repr(error))
                self.poisoned = True
                raise
            finally:
                if gate is not None:
                    gate.active = False
                if runtime is not None:
                    runtime.connected = False
                    runtime.stopped = True
                try:
                    try:
                        if runtime is not None:
                            await runtime.router.close()
                    finally:
                        if store is not None:
                            store.close()
                except BaseException as error:
                    row.update(passed=False, cleanup_error=repr(error))
                    self.poisoned = True
                    raise
                finally:
                    self.busy = False
                    self.save_report()

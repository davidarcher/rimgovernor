"""Persistent replacement session: one game writer, live planner, player inbox."""
import asyncio
import time
import uuid
from pathlib import Path

from .bridge import bridge_session
from .bridge_game import BridgeGame, WRITES
from .bridge_observation import observe
from .config import Settings
from .model import LocalModel
from .planner import Planner


class BridgeRuntime:
    def __init__(self, store, root, *, fresh=False, settings=None, model_factory=LocalModel):
        self.store, self.root, self.fresh = store, Path(root).resolve(), fresh
        self.settings = settings or Settings()
        self.model = model_factory(self.settings)
        self.planner = Planner(self)
        self.colony = uuid.uuid4().hex
        self.chat, self.plan = [], {'long': '', 'short': ''}
        self.chat_revision = 0
        self.handled_revision = 0
        self.mode, self.phase = 'manual', 'Connecting'
        self.connected = self.stopped = self.resume_after_review = False
        self.batch = self.game = self.bridge = self.review_task = None
        self.camera_path = None
        self.camera_version = 0
        self.clock = {}
        self.counters = {'tools': 0, 'actions': 0, 'model_calls': 0, 'input_tokens': 0, 'output_tokens': 0}
        self.lock = asyncio.Lock()
        self.wake = asyncio.Event()
        self.shutdown = asyncio.Event()

    def persist(self):
        self.store.set('bridge:'+self.colony, {'chat': self.chat, 'plan': self.plan})

    def note(self, kind, text, **extra):
        return self.store.event(self.colony, kind, text=text, **extra)

    def reply(self, text):
        event = self.note('summary', text)
        self.chat.append(dict(event, ts=event['at'], revision=self.chat_revision))
        self.persist()

    async def steer(self, text):
        text = text.strip()
        if not text or len(text) > 4000:
            raise ValueError('Send a message between 1 and 4000 characters')
        self.chat_revision += 1
        event = self.note('human', text)
        self.chat.append(dict(event, ts=event['at'], revision=self.chat_revision))
        self.persist()
        self.wake.set()

    def usage(self, usage):
        self.counters['model_calls'] += 1
        self.counters['input_tokens'] += usage.get('prompt_tokens', 0)
        self.counters['output_tokens'] += usage.get('completion_tokens', 0)

    async def model_progress(self, values):
        self.phase = values.get('phase', 'Thinking')

    async def set_mode(self, mode):
        if mode not in ('manual', 'automate'):
            raise ValueError('Choose Manual or Automate')
        async with self.lock:
            if not self.connected:
                raise ValueError('Wait for the bridge to connect')
            self.mode = mode
            self.resume_after_review = mode == 'automate'
            await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        await self.steer('Control changed to '+mode+'. '+('Continue the colony plan.' if mode == 'automate' else 'Discuss and inspect only; do not issue game orders.'))

    async def native(self, name, arguments, *, expected_revision=None):
        async with self.lock:
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived; this call was not executed')
            result = await self.game.invoke(name, arguments, allow_write=self.mode == 'automate')
            self.counters['tools'] += 1
            self.note('tool_result', name, arguments=arguments, result=result)
            if name in WRITES and not arguments.get('dryRun', False):
                self.counters['actions'] += 1
                self.note('action', name, detail='Native receipt recorded; completion is determined from game state')
                if name == 'home/zone_cells':
                    verification = await self.game.query('home/list_zones')
                elif name in ('home/place_building', 'rimworld/apply_architect_designator'):
                    verification = await self.game.invoke('rimworld/get_cell_info', {'x': arguments['x'], 'z': arguments['z']})
                else:
                    verification = await self.game.query('home/status')
                result = {'receipt': result, 'observed_after': verification,
                          'meaning': 'Native post-command readback. Jobs/blueprints may still need pawn work.'}
            return result

    async def start(self):
        self.task = asyncio.create_task(self.run())

    async def stop(self):
        self.stopped = True
        self.shutdown.set()
        if self.review_task:
            self.review_task.cancel()
            await asyncio.gather(self.review_task, return_exceptions=True)
        await self.task
        await self.model.close()

    async def review(self):
        try:
            self.phase = 'Inspecting colony'
            async with self.lock:
                self.batch = await observe(self.game)
            await self.planner.play_bridge()
            async with self.lock:
                if self.resume_after_review and self.mode == 'automate':
                    await self.bridge.call('rimworld/set_time_speed', speed='Normal', ultraSpeedBoost=False)
                    self.resume_after_review = False
            self.phase = 'Playing' if self.mode == 'automate' else 'Manual'
        except asyncio.CancelledError:
            raise
        except Exception as error:
            self.phase = 'Needs attention'
            self.reply('Review stopped: '+str(error))
            self.mode = 'manual'
            self.resume_after_review = False
            self.handled_revision = self.chat_revision
        finally:
            if self.chat_revision > self.handled_revision:
                self.wake.set()

    async def run(self):
        executable = self.root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe'
        try:
            async with bridge_session(executable, self.root/'config') as bridge:
                self.bridge = bridge
                if self.fresh:
                    await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                if self.fresh:
                    await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000)
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                self.game = BridgeGame(bridge)
                self.batch = await observe(self.game)
                self.connected, self.phase = True, 'Manual'
                last_review = 0
                while not self.stopped:
                    if self.review_task is None or self.review_task.done():
                        if self.wake.is_set() or (self.mode == 'automate' and time.monotonic()-last_review > 30):
                            self.wake.clear()
                            last_review = time.monotonic()
                            self.review_task = asyncio.create_task(self.review())
                    try:
                        async with self.lock:
                            status = await self.game.query('home/status', colonists=False, threats=False)
                            self.clock = status['time']
                            image = await bridge.call('rimworld/take_screenshot', fileName='rimbot-live', includeTargets=False, suppressMessage=True)
                            candidate = Path(image.structuredContent['path']).resolve()
                            if candidate.is_relative_to(self.root) and candidate.is_file():
                                self.camera_path = candidate
                                self.camera_version += 1
                    except Exception as error:
                        self.note('camera_error', str(error))
                    try:
                        await asyncio.wait_for(self.shutdown.wait(), 2)
                    except TimeoutError:
                        pass
        except Exception as error:
            self.phase = 'Connection failed'
            self.reply(str(error))
        finally:
            self.connected = False

    def public(self):
        summary = self.batch.summary if self.batch else None
        feed = self.chat[-80:]
        recent = next((m for m in reversed(feed) if m['kind'] == 'summary'), None)
        return {'sessionId': self.colony, 'goals': self.plan, 'mood': 'thinking' if self.review_task and not self.review_task.done() else 'happy',
            'status': {'phase': 'core', 'turn': self.counters['model_calls'],
                       'label': self.phase+f" · {self.counters['tools']} calls · {self.counters['actions']} orders"},
            'game': {'tick': self.clock.get('ticksGame', summary.end_tick if summary else None), 'paused': self.clock.get('paused', True),
                     'stale': not self.connected, 'wallTs': time.time()},
            'lastSummary': recent, 'feed': feed, 'mode': self.mode, 'connected': self.connected,
            'cameraVersion': self.camera_version, 'counters': self.counters,
            'observation': summary.model_dump() if summary else None}

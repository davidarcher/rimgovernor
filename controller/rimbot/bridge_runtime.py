"""Persistent replacement session: one game writer, live planner, player inbox."""
import asyncio
import time
import uuid
from pathlib import Path

from .bridge import bridge_session
from .clock_control import PlayClock, HOLD_REASONS
from .bridge_game import BridgeGame, WRITES, is_write
from .bridge_observation import observe
from .config import Settings, ModelRole, load_model_routing
from .model_router import ModelRouter
from .colony_plan import ColonyPlan, Decision, Failure, NativeOperation
from .medical_outcome import patient_outcome, rescue_outcome
from .consultation import Consultations
from .hands import Hands, validate_geometry
from .strategic_state import StrategicState, projection
from .model import LocalModel
from .planner import Planner
from .projects import ProjectBook, ProjectSpec
from .receipts import verdict_line, _outcome


def failure_text(error):
    if isinstance(error, BaseExceptionGroup):
        return '; '.join(failure_text(item) for item in error.exceptions)
    return str(error)


class BridgeRuntime:
    def __init__(self, store, root, *, fresh=False, settings=None, model_factory=LocalModel, routing=None, headless=False):
        self.headless = headless
        self.store, self.root, self.fresh = store, Path(root).resolve(), fresh
        self.settings = settings or Settings()
        self.router = ModelRouter(routing or load_model_routing(self.settings), store, model_factory)
        self.consultations = Consultations(self.router)
        self.advice = {}
        self.current_plan = ColonyPlan()
        self.strategic_state = StrategicState()
        self.hands = Hands()
        self.execution_task = None
        self.planner = Planner(self)
        self.colony = uuid.uuid4().hex
        self.chat, self.plan = [], {'long': '', 'short': ''}
        self.projects = ProjectBook()
        self.identity = None
        self.context_token = None
        self.draft_owners = {}
        self.chat_revision = 0
        self.handled_revision = 0
        self.mode, self.phase = 'manual', 'Connecting'
        self.connected = self.stopped = self.resume_after_review = False
        self.batch = self.game = self.bridge = self.review_task = None
        self.video_viewers = {}
        self.render_state = {}
        self.camera_path = None
        self.camera_version = 0
        self.clock = {}
        self.supervisor = None
        self.clock_task = None
        self.clock_events = []
        self.counters = {'tools': 0, 'actions': 0, 'model_calls': 0, 'input_tokens': 0, 'output_tokens': 0}
        self.lock = asyncio.Lock()
        self.wake = asyncio.Event()
        self.shutdown = asyncio.Event()

    def persist(self):
        self.store.set('bridge:'+self.colony, {'chat': self.chat, 'plan': self.plan, 'projects': self.projects.dump(),
            'chat_revision': self.chat_revision, 'handled_revision': self.handled_revision,
            'draft_owners': self.draft_owners, 'current_plan': self.current_plan.model_dump(),
            'strategic_state': self.strategic_state.dump(), 'advice': self.advice})

    async def sync_identity(self):
        if self.game is None:
            raise ValueError('Wait for the native colony connection')
        identity = await self.game.query('home/colony_identity')
        key = identity['colonyId']+':'+str(identity['mapId'])
        token = key+':'+identity['loadToken']
        changed = token != self.context_token
        if changed:
            saved = self.store.get('bridge:'+key, {})
            self.colony, self.identity, self.context_token = key, identity, token
            self.supervisor = PlayClock(self.bridge) if self.bridge else None
            self.clock_events.clear()
            self.chat = saved.get('chat', [])
            self.plan = saved.get('plan', {'long': '', 'short': ''})
            self.projects = ProjectBook(saved.get('projects', []))
            self.current_plan = ColonyPlan.model_validate(saved.get('current_plan', {}))
            self.strategic_state = StrategicState(saved.get('strategic_state'))
            self.advice = saved.get('advice', {})
            # Ownership applies only to this live load, never to an older save.
            self.draft_owners = {k: v for k, v in saved.get('draft_owners', {}).items() if v == token}
            self.chat_revision = saved.get('chat_revision', 0)+1
            self.handled_revision = self.chat_revision
            self.mode, self.resume_after_review = 'manual', False
            self.wake.clear()
            self.camera_path, self.camera_version = None, 0
            self.persist()
        return changed

    def signal(self, kind, evidence):
        before = {e['key'] for e in self.strategic_state.pending}
        self.strategic_state.signal(kind, evidence)
        if self.mode == 'automate' and {e['key'] for e in self.strategic_state.pending} != before:
            self.chat_revision += 1
            self.wake.set()

    def update_strategy_state(self):
        before = {e['key'] for e in self.strategic_state.pending}
        self.strategic_state.update(self.batch)
        if self.mode == 'automate' and {e['key'] for e in self.strategic_state.pending} != before:
            self.chat_revision += 1
            self.wake.set()

    async def inspect_native(self, name, arguments):
        if is_write(name, arguments):
            raise PermissionError('Strategist and advisers cannot execute native writes; commit a plan for Hands')
        async with self.lock:
            return await self.game.invoke(name, arguments, allow_write=False)

    async def consult(self, role, question, sections, include_image=False, *, expected_token=None, expected_revision=None):
        from .config import ModelRole
        import base64
        async with self.lock:
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Consultation context changed')
            selected = projection(self, sections)
            image = None
            if include_image:
                if ModelRole(role) != ModelRole.ARCHITECT or not self.camera_path:
                    raise ValueError('A current architect image is not available')
                image = 'data:image/png;base64,'+base64.b64encode(self.camera_path.read_bytes()).decode()
        result = await self.consultations.ask(role, question, selected, self.model_progress, image)
        async with self.lock:
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Consultation is stale; no recommendation retained')
            self.advice[result['id']] = result
            self.advice = dict(list(self.advice.items())[-32:])
            self.note('consultation', result['role']+': '+result['report']['answer'], consultation_id=result['id'])
            self.persist()
        return result

    async def visual_review(self, question, *, expected_token, expected_revision):
        import base64
        import hashlib
        from .visual_review import review
        if self.headless:
            raise ValueError('Visual review needs rendered mode; no image exists in headless mode')
        if not self.router.enabled(ModelRole.ARCHITECT):
            raise ValueError('Configure the optional architect with a vision-capable local model')
        async def check():
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Visual review context changed; report discarded')
        async with self.lock:
            await check()
            # Fresh capture of the current view only: never move the player's camera.
            await self.bridge.call('home/render_demand',seconds=15)
            await asyncio.sleep(.3)
            capture=await self.bridge.call('rimworld/take_screenshot',fileName='rimbot-review-'+uuid.uuid4().hex,
                includeTargets=False,suppressMessage=True)
            data=Path(capture.structuredContent['path']).read_bytes()
            if not data.startswith(b'\x89PNG\r\n\x1a\n') or len(data)>12*1024*1024:
                raise ValueError('Expected a bounded native PNG screenshot')
            status=await self.game.query('home/status',colonists=False,threats=False)
            await check()
            source={'load_token':self.context_token,'tick':status['time']['ticksGame'],
                'image_sha256':hashlib.sha256(data).hexdigest(),'view':'current player camera',
                'captured_at':time.time(),'tick_note':'Read after screenshot; not an atomic state snapshot'}
        result=await review(self.router,question,'data:image/png;base64,'+base64.b64encode(data).decode(),source,self.model_progress)
        async with self.lock:
            await check()
            self.advice[result['id']]=result
            self.advice=dict(list(self.advice.items())[-32:])
            self.note('consultation','Visual review: '+result['report']['answer'],consultation_id=result['id'])
            self.persist()
        return result

    async def scout(self, question, sections, *, expected_token, expected_revision):
        from .scout import investigate, SCOUT_READS
        async def check():
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise InterruptedError('Scout context changed; investigation discarded')
        async with self.lock:
            await check()
            selected = projection(self, sections)
        async def describe(name):
            if name not in SCOUT_READS:
                raise PermissionError('Scout cannot access this capability')
            async with self.lock:
                await check()
                return await self.game.describe(name)
        async def read(name, arguments):
            if name not in SCOUT_READS or is_write(name, arguments):
                raise PermissionError('Scout cannot write')
            async with self.lock:
                await check()
                value = await self.game.invoke(name, arguments, allow_write=False)
                await check()
                return value
        result, audit = await investigate(self.router, question, selected, describe, read, self.model_progress)
        async with self.lock:
            await check()
            self.advice[result['id']] = result
            self.advice = dict(list(self.advice.items())[-32:])
            self.note('scout_evidence', 'Read-only investigation evidence', consultation_id=result['id'], observations=audit)
            self.note('consultation', result['report']['answer'], consultation_id=result['id'], investigation=result['investigation'])
            self.persist()
        return result

    async def commit_strategy(self, decision, *, actor, expected_token, expected_revision):
        if actor != ModelRole.STRATEGIST:
            raise PermissionError('Only the strategist can commit a plan')
        async with self.lock:
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Colony or direction changed; decision was not committed')
            if decision.plan:
                validate_geometry(decision.plan)
            for identity in decision.used_consultations:
                if identity not in self.advice or self.advice[identity]['load_token'] != self.context_token:
                    raise ValueError('Consultation is missing or belongs to another loaded game')
            for step_id in decision.retry_steps:
                progress = self.current_plan.progress.get(step_id)
                if not progress or progress.state != 'blocked' or not progress.failure or not progress.failure.retryable:
                    raise ValueError('Step is not safely retryable: '+step_id)
            changed = self.current_plan.commit(decision, actor=actor, tick=self.batch.summary.end_tick)
            for step_id in decision.retry_steps:
                progress = self.current_plan.progress[step_id]
                progress.state, progress.failure = 'pending', None
            for identity in decision.used_consultations:
                if not self.advice[identity]['used']:
                    self.router.used(self.advice[identity]['role'])
                    self.advice[identity]['used'] = True
            self.plan = {'long':self.current_plan.spec.long_term, 'short':self.current_plan.spec.right_now}
            self.strategic_state.decided()
            self.handled_revision = expected_revision
            self.wake.clear()
            self.note('plan_decision', decision.rationale, revision=self.current_plan.revision,
                changed=changed, disposition=decision.disposition, assessment=decision.assessment)
            self.reply(decision.reply)
            self.persist()
            return {'committed':True, 'changed':changed, 'revision':self.current_plan.revision}

    def reconcile_plan(self):
        for step in self.current_plan.spec.steps:
            progress = self.current_plan.progress[step.id]
            if (isinstance(step.action, NativeOperation) and step.action.completion in ('patient_tended', 'patient_in_bed')
                    and progress.state == 'waiting' and self.batch):
                if self.batch.started_at <= progress.issued.get('0', {}).get('issued_at', float('inf')):
                    continue  # Never reconcile against a batch started before the order.
                check = patient_outcome if step.action.completion == 'patient_tended' else rescue_outcome
                outcome = check(step.action.arguments, self.batch.native.get('pawns', {}).get('pawns', []))
                if outcome != 'waiting':
                    progress.state = 'blocked' if isinstance(outcome, Failure) else 'complete'
                    progress.failure = outcome if isinstance(outcome, Failure) else None
                    detail = outcome.detail if isinstance(outcome, Failure) else ('Patient no longer needs tending.'
                        if step.action.completion == 'patient_tended' else 'Patient observed in a bed.')
                    self.note('execution_blocked' if progress.failure else 'execution', step.title+': '+detail)
                    self.signal('plan.step_'+progress.state, {'step': step.id, 'evidence': detail})
                continue
            if not progress.project_id or progress.state in ('blocked', 'cancelled'):
                continue
            row = next((p for p in self.projects.rows if p.id == progress.project_id), None)
            if row is None:
                continue
            old = progress.state
            if row.state == 'complete':
                progress.state = 'complete'
            elif row.state in ('planned', 'blocked', 'cancelled'):
                progress.state = 'blocked'
                progress.failure = Failure(code='plan_invalidated', detail=row.evidence)
            else:
                progress.state = 'waiting'
            if old != progress.state:
                self.signal('plan.step_'+progress.state, {'step':step.id, 'evidence':row.evidence})

    async def cancel_plan_step(self, identity):
        async with self.lock:
            await self.sync_identity()
            self.current_plan.cancel(identity)
            self.persist()
        await self.steer('Player cancelled plan step '+identity+'. Existing game orders are unchanged.')

    async def ensure_context(self, token):
        async with self.lock:
            await self.sync_identity()
            if token != self.context_token:
                raise ValueError('Colony or loaded save changed; stale review stopped')

    async def memory(self, operation, id, text=None, evidence=None, *, expected_token, expected_revision):
        from .memory import update_memory
        async with self.lock:
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Colony or direction changed; memory request discarded')
            result = update_memory(self.strategic_state.memories, operation, id, text, evidence,
                tick=self.strategic_state.current.get('tick'), load_token=self.context_token)
            if operation != 'read':
                self.persist()
                self.note('memory', f'{operation.capitalize()} memory: {id}', memory=result)
            return result

    async def forget_memory(self, identity, session_id, version):
        from .strategic_state import fingerprint
        async with self.lock:
            await self.sync_identity()
            if session_id != self.context_token:
                raise ValueError('Colony changed; refresh the notebook')
            note = self.strategic_state.memories.get(identity)
            if note is None or fingerprint(note) != version:
                raise ValueError('Note changed or was removed; refresh the notebook')
            del self.strategic_state.memories[identity]
            # Invalidate pending strategy/hands work just like fresh player direction.
            self.chat_revision += 1
            self.strategic_state.signal('player.forgot_memory', {'id': identity})
            self.note('memory', 'Player forgot memory: '+identity)
            self.persist()
            if self.mode == 'automate':
                self.wake.set()
            return {'deleted': identity}

    def public_memories(self):
        from .strategic_state import fingerprint
        return [dict(id=key, text=note['text'], evidence=note['evidence'], tick=note['tick'],
            previous_load=note['load_token'] != self.context_token, version=fingerprint(note))
            for key, note in self.strategic_state.memories.items()]

    async def project_update(self, spec, expected_token=None):
        async with self.lock:
            await self.sync_identity()
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed')
            row = self.projects.upsert(spec)
            await self.projects.reconcile(self.game)
            self.persist()
            return row.model_dump()

    async def cancel_project(self, identity):
        async with self.lock:
            await self.sync_identity()
            row = self.projects.cancel(identity)
            self.persist()
        await self.steer('Cancelled project: '+row.title+'. Do not recreate it; existing game orders are unchanged.')
        return row.model_dump()

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

    async def release_drafts(self, selected=None, guard=None):
        """Caller holds the writer lock. Retain obligations if cleanup is uncertain."""
        result = {'released': [], 'not_owned': [], 'failed': {}}
        for pawn in dict.fromkeys(selected if selected is not None else self.draft_owners):
            if guard:
                await guard()
            if self.draft_owners.get(pawn) != self.context_token:
                result['not_owned'].append(pawn)
                continue
            try:
                args = {'action': 'resolve', 'pawn': pawn, 'dryRun': True}
                state = await self.game.invoke('home/order', args, allow_write=False)
                if guard:
                    await guard()
                if state.get('pawn', {}).get('thingId') != pawn:
                    raise ValueError('Pawn identity was not confirmed')
                if state['pawn'].get('drafted') is not False:
                    if guard:
                        await guard()
                    await self.game.invoke('home/order', {'action': 'undraft', 'pawn': pawn, 'dryRun': False}, allow_write=True)
                    state = await self.game.invoke('home/order', args, allow_write=False)
                    if guard:
                        await guard()
                if state.get('pawn', {}).get('thingId') != pawn or state.get('pawn', {}).get('drafted') is not False:
                    raise ValueError('Undraft was not confirmed')
                del self.draft_owners[pawn]
                result['released'].append(pawn)
                self.persist()
            except InterruptedError:
                raise
            except Exception as error:
                result['failed'][pawn] = failure_text(error)
                self.note('blocker', 'Could not release AI-drafted pawn '+pawn+': '+failure_text(error))
        return result

    async def stand_down(self, pawn_ids, *, expected_token, expected_revision, expected_plan_revision):
        async with self.lock:
            async def guard():
                await self.sync_identity()
                if (self.mode != 'automate' or self.context_token != expected_token
                        or self.chat_revision != expected_revision
                        or self.current_plan.revision != expected_plan_revision):
                    raise InterruptedError('Stand-down intent changed; remaining pawns were not touched')
            await guard()
            return await self.release_drafts(pawn_ids, guard)

    async def halt(self):
        """Stop automation and its draft obligations; never silently claim success."""
        self.mode, self.resume_after_review = 'manual', False
        try:
            if self.supervisor:
                await self.supervisor.change('Paused')
            else:
                await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        except Exception as error:
            self.note('blocker', 'Could not pause the game: '+failure_text(error))
        await self.release_drafts()

    async def set_mode(self, mode):
        if mode not in ('manual', 'automate'):
            raise ValueError('Choose Manual or Automate')
        async with self.lock:
            if not self.connected:
                raise ValueError('Wait for the bridge to connect')
            await self.sync_identity()
            if mode == 'manual':
                await self.halt()
            if self.supervisor:
                await self.supervisor.change('Paused')
                # Only explicit player control clears an external pause latch.
                self.supervisor.allow_resume()
            else:
                await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            self.mode = mode
            self.resume_after_review = mode == 'automate'
        await self.steer('Control changed to '+mode+'. '+('Continue the colony plan.' if mode == 'automate' else 'Discuss and inspect only; do not issue game orders.'))

    async def control_clock(self, speed, *, mode='colony', ignored_hostiles='', ignored_downed='', expected_revision=None, expected_token=None, expected_plan_revision=None):
        async with self.lock:
            await self.sync_identity()
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed; clock was not changed')
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New direction arrived; clock was not changed')
            if expected_plan_revision is not None and expected_plan_revision != self.current_plan.revision:
                raise InterruptedError('Committed plan changed; clock was not changed')
            if self.mode != 'automate':
                raise ValueError('Automation is off; clock was not changed')
            self.resume_after_review = False
            result = await self.supervisor.change(speed, mode=mode,
                ignored_hostiles=ignored_hostiles, ignored_downed=ignored_downed)
            self.note('action', 'Clock: '+(result.get('stopReason') or speed), result=result)
            return result

    async def watch_clock(self):
        # Separate from inference and the game-writer lock: slow thinking cannot
        # consume the native lease. If communication stalls, RimWorld pauses.
        failed_supervisor = None
        while not self.stopped:
            supervisor = self.supervisor
            if supervisor:
                try:
                    rows = await supervisor.poll()
                    failed_supervisor = None
                    if supervisor is self.supervisor:
                        self.clock_events.extend(rows)
                except Exception as error:
                    if supervisor is self.supervisor and failed_supervisor is not supervisor:
                        failed_supervisor = supervisor
                        self.clock_events.append({'kind': 'clock_error', 'detail': failure_text(error)})
            try:
                await asyncio.wait_for(self.shutdown.wait(), 3)
            except TimeoutError:
                pass

    def receive_clock_events(self):
        events, self.clock_events = self.clock_events, []
        for event in events:
            kind = event['kind']
            if kind in ('started', 'heartbeat', 'paused', 'requested_pause', 'speed_changed'):
                continue
            if (self.supervisor and kind in HOLD_REASONS
                    and (event.get('epoch'), kind) == self.supervisor.acknowledged_stop):
                continue
            saved_event = self.note('clock_event', event['detail'], native_event=event)
            self.strategic_state.signal('native.'+kind, event)
            self.resume_after_review = False
            if kind in HOLD_REASONS or kind == 'clock_error':
                self.mode = 'manual'
                self.phase = 'Clock held'
            # Deliver evidence to an in-flight planner, invalidate stale calls,
            # and wake an idle planner without claiming this is a player message.
            self.chat_revision += 1
            self.chat.append(dict(saved_event, revision=self.chat_revision))
            self.wake.set()
        if events:
            self.persist()

    async def native(self, name, arguments, *, expected_revision=None, expected_token=None, expected_plan_revision=None, reconcile=True):
        if name == 'rimworld/set_time_speed':
            if set(arguments) - {'speed', 'ultraSpeedBoost'}:
                raise ValueError('Unknown time control argument')
            if arguments.get('ultraSpeedBoost'):
                raise ValueError('Use normal game speeds')
            return await self.control_clock(arguments.get('speed'), expected_revision=expected_revision, expected_token=expected_token, expected_plan_revision=expected_plan_revision)
        async with self.lock:
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived; this call was not executed')
            await self.sync_identity()
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed; no command sent')
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived; no command sent')
            if expected_plan_revision is not None and expected_plan_revision != self.current_plan.revision:
                raise InterruptedError('Committed plan changed; no order sent')
            if name == 'home/order' and self.mode == 'automate' and not arguments.get('dryRun', False):
                # Resolve before writing; persist intent even if the write loses its receipt.
                if arguments.get('action') in ('draft', 'goto', 'attack', 'tend'):
                    before = await self.game.invoke('home/order', {'action': 'resolve', 'pawn': arguments.get('pawn'), 'dryRun': True}, allow_write=True)
                    pawn = before.get('pawn') or {}
                    if pawn.get('drafted') is False and pawn.get('thingId'):
                        self.draft_owners[str(pawn['thingId'])] = self.context_token
                        self.persist()
                    if (arguments.get('action') == 'tend'
                            and pawn.get('thingId') and self.context_token
                            and self.draft_owners.get(str(pawn.get('thingId'))) == self.context_token):
                        # This controller has persisted the cleanup obligation.
                        # The model need not know the native lifecycle handshake.
                        arguments = dict(arguments)
                        arguments.setdefault('allowPersistentDraft', True)
            result = await self.game.invoke(name, arguments, allow_write=self.mode == 'automate')
            if name == 'home/order' and not arguments.get('dryRun', False):
                pawn_after = result.get('pawn') or {}
                if pawn_after.get('drafted') is False:
                    self.draft_owners.pop(str(pawn_after.get('thingId')), None)
                    self.persist()
            self.counters['tools'] += 1
            self.note('tool_result', name, arguments=arguments, result=result)
            if is_write(name, arguments) and not arguments.get('dryRun', False):
                placed = name != 'home/place_building' or _outcome(result) == 'placed'
                self.counters['actions'] += int(placed)
                self.note('action' if placed else 'receipt', verdict_line(result) if name == 'home/place_building' else name, detail='Native receipt; completion comes from game state')
                if name == 'home/zone_cells':
                    verification = await self.game.query('home/list_zones')
                elif name in ('home/place_building', 'rimworld/apply_architect_designator'):
                    verification = await self.game.invoke('rimworld/get_cell_info', {'x': arguments['x'], 'z': arguments['z']})
                elif name == 'home/trade':
                    verification = await self.game.invoke('home/trade', {'action': 'status'})
                elif name == 'home/research':
                    verification = await self.game.invoke('home/research', {'dryRun': True})
                    selected = ((result.get('write') or {}).get('resolved') or {}).get('defName')
                    current = [verification.get('current')] + list((verification.get('currentByCategory') or {}).values())
                    if not selected or not any(isinstance(p,dict) and p.get('defName') == selected for p in current):
                        raise ValueError('Research selection was not confirmed by fresh native readback')
                elif name == 'rimworld/dismiss_letter':
                    verification = await self.game.invoke('rimworld/list_letters', {'limit': 1000})
                    from .notifications import verify_dismissal
                    verify_dismissal(arguments['letterId'], result, verification)
                else:
                    verification = await self.game.query('home/status')
                    self.clock = verification['time']
                    if name == 'rimworld/set_time_speed':
                        self.resume_after_review = False
                result = {'receipt': result, 'observed_after': verification,
                          'meaning': 'Native post-command readback. Jobs/blueprints may still need pawn work.',
                          'clock': 'paused' if self.clock.get('paused') is True else 'running' if self.clock.get('paused') is False else 'unknown'}
                if reconcile:
                    await self.projects.reconcile(self.game)
                self.persist()
            return result

    async def start(self):
        self.task = asyncio.create_task(self.run())

    async def stop(self):
        self.stopped = True
        self.shutdown.set()
        if self.review_task:
            self.review_task.cancel()
            await asyncio.gather(self.review_task, return_exceptions=True)
        if self.execution_task:
            self.execution_task.cancel()
            await asyncio.gather(self.execution_task, return_exceptions=True)
        await self.task
        await self.router.close()

    async def review(self):
        try:
            self.phase = 'Inspecting colony'
            async with self.lock:
                await self.sync_identity()
                self.batch = await observe(self.game)
                self.update_strategy_state()
                await self.projects.reconcile(self.game)
                self.reconcile_plan()
                self.persist()
            await self.planner.play_bridge()
            async with self.lock:
                if self.resume_after_review and self.mode == 'automate':
                    await self.supervisor.change('Normal')
                    self.resume_after_review = False
            self.phase = 'Playing' if self.mode == 'automate' else 'Manual'
        except asyncio.CancelledError:
            raise
        except Exception as error:
            self.phase = 'Needs attention'
            self.reply('Review stopped: '+failure_text(error))
            async with self.lock:
                await self.halt()
            self.handled_revision = self.chat_revision
        finally:
            if self.chat_revision > self.handled_revision:
                self.wake.set()

    async def run(self):
        executable = self.root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe'
        try:
            from .headless import prepare
            configuration = prepare(self.root) if self.headless else self.root/'config'
            async with bridge_session(executable, configuration) as bridge:
                self.bridge = bridge
                if self.fresh:
                    await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                if self.fresh:
                    await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000, ignoreModCompatibility=self.headless)
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                self.game = BridgeGame(bridge)
                await self.sync_identity()
                await self.projects.reconcile(self.game)
                self.persist()
                self.batch = await observe(self.game)
                self.connected, self.phase = True, 'Manual'
                self.clock_task = asyncio.create_task(self.watch_clock())
                self.update_strategy_state()
                last_reconcile = 0
                while not self.stopped:
                    if self.review_task is None or self.review_task.done():
                        if self.wake.is_set():
                            self.wake.clear()
                            self.review_task = asyncio.create_task(self.review())
                    if (self.mode == 'automate' and self.handled_revision >= self.chat_revision
                            and (self.execution_task is None or self.execution_task.done())):
                        self.execution_task = asyncio.create_task(self.hands.advance(self))
                    try:
                        async with self.lock:
                            if await self.sync_identity():
                                self.batch = await observe(self.game)
                                await self.projects.reconcile(self.game)
                                self.persist()
                            self.receive_clock_events()
                            status = await self.game.query('home/status', colonists=False, threats=False)
                            self.clock = status['time']
                            if time.monotonic()-last_reconcile > 10:
                                self.batch = await observe(self.game)
                                self.update_strategy_state()
                                await self.projects.reconcile(self.game)
                                self.reconcile_plan()
                                if self.strategic_state.pending and self.mode == 'automate':
                                    self.wake.set()
                                self.persist()
                                last_reconcile = time.monotonic()
                            if not self.headless:
                                watching = any(until > time.monotonic() for until in self.video_viewers.values())
                                lease = await bridge.call('home/render_demand', seconds=8 if watching else 0)
                                self.render_state = lease.structuredContent
                            if not self.headless and watching:
                                await asyncio.sleep(.15)
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
                async with self.lock:
                    await self.halt()
        except Exception as error:
            import logging
            logging.exception('Native bridge connection failed')
            self.phase = 'Connection failed'
            self.reply(failure_text(error))
        finally:
            if self.clock_task:
                self.clock_task.cancel()
                await asyncio.gather(self.clock_task, return_exceptions=True)
            self.connected = False

    def public(self):
        summary = self.batch.summary if self.batch else None
        feed = self.chat[-80:]
        recent = next((m for m in reversed(feed) if m['kind'] == 'summary'), None)
        return {'memories': self.public_memories(), 'sessionId': self.context_token or self.colony, 'projects': self.projects.dump(), 'goals': self.plan, 'mood': 'thinking' if self.review_task and not self.review_task.done() else 'happy',
            'status': {'phase': 'core', 'turn': self.counters['model_calls'],
                       'label': self.phase+f" · {self.counters['tools']} calls · {self.counters['actions']} orders"},
            'game': {'tick': self.clock.get('ticksGame', summary.end_tick if summary else None), 'paused': self.clock.get('paused', True),
                     'stale': not self.connected, 'wallTs': time.time()},
            'lastSummary': recent, 'feed': feed, 'mode': self.mode, 'connected': self.connected,
            'currentPlan': {'revision': self.current_plan.revision, 'chosen_tick': self.current_plan.chosen_tick,
                'rationale': self.current_plan.rationale, 'goals': self.current_plan.spec.goals,
                'constraints': self.current_plan.spec.constraints, 'risks': self.current_plan.spec.risks,
                'steps': [dict(id=s.id, title=s.title, priority=s.priority, action=s.action.kind,
                    completion=s.completion_criteria, state=self.current_plan.progress[s.id].state,
                    issued=len(self.current_plan.progress[s.id].issued),
                    failure=self.current_plan.progress[s.id].failure.model_dump() if self.current_plan.progress[s.id].failure else None)
                    for s in self.current_plan.spec.steps]}, 'modelRoles': self.router.metrics,
            'clockSupervisor': self.supervisor.state if self.supervisor else {},
            'rendering': self.render_state, 'headless': self.headless, 'cameraVersion': self.camera_version, 'counters': self.counters,
            'observation': summary.model_dump() if summary else None}

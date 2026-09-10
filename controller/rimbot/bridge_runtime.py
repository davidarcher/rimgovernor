"""Persistent replacement session: one game writer, live planner, player inbox."""
import asyncio
import time
import uuid
from pathlib import Path

from .bridge import bridge_session, gabs_executable
from .headless import rendered_headless_mismatch
from .clock_control import PlayClock, HOLD_REASONS
from .bridge_game import BridgeGame, WRITES, is_write
from .bridge_observation import observe
from .config import Settings, ModelRole, load_model_routing
from .model_router import ModelRouter
from .colony_plan import ColonyPlan, Decision, Failure, NativeOperation
from .medical_outcome import patient_outcome, rescue_outcome, pawn_order_outcome
from .medical_recovery import recover_treatment
from .consultation import Consultations
from .hands import Hands, validate_geometry
from .native_contracts import validate_native_steps, validate_stand_down_steps
from .construction_preflight import preflight_construction
from .strategic_state import StrategicState, projection
from .model import LocalModel
from .planner import Planner
from .colony_controller import ColonyController
from .controller_settings import settings_state
from .resource_accounting import validate_allocations
from .projects import ProjectBook, ProjectSpec
from .receipts import verdict_line, _outcome


def failure_text(error):
    if isinstance(error, BaseExceptionGroup):
        return '; '.join(failure_text(item) for item in error.exceptions)
    return str(error)


class BridgeRuntime:
    def __init__(self, store, root, *, fresh=False, settings=None, model_factory=LocalModel, routing=None, headless=False, resume=None):
        self.headless = headless
        self.store, self.root, self.fresh = store, Path(root).resolve(), fresh
        self.resume = resume
        if resume:
            from .session_checkpoint import read_checkpoint
            checkpoint = read_checkpoint(resume)
            if Path(checkpoint['root']).resolve() != self.root or checkpoint['headless'] != headless or not fresh:
                raise ValueError('Resume requires its matching owned root and render mode')
        self.settings = settings or Settings()
        self.router = ModelRouter(routing or load_model_routing(self.settings), store, model_factory)
        self.consultations = Consultations(self.router)
        self.advice = {}
        self.current_plan = ColonyPlan()
        self.strategic_state = StrategicState()
        self.hands = Hands()
        self.manual_requests = []
        self.manual_execution = None
        self.execution_task = None
        self.deliberating = False
        self.execution_window_end = None
        self.execution_wait_explicit = False
        self.planner = Planner(self)
        self.controller = ColonyController(self)
        self.colony = uuid.uuid4().hex
        self.chat, self.plan = [], {'long': '', 'short': ''}
        self.projects = ProjectBook()
        self.identity = None
        self.context_token = None
        self.draft_owners = {}
        self.ui_targets = {}
        self.chat_revision = 0
        self.handled_revision = 0
        self.mode, self.phase = 'manual', 'Connecting'
        self.connected = self.stopped = self.resume_after_review = False
        self.batch = self.game = self.bridge = self.review_task = None
        self.video_viewers = {}
        self.streaming_viewers = set()
        self.render_state = {}
        self.camera_path = None
        self.camera_bytes = None
        self.camera_version = 0
        self.camera_error = ''
        self.camera_captured_at = 0
        self.clock = {}
        self.supervisor = None
        self.clock_task = None
        self.clock_events = []
        self.counters = {'planner_tools': 0, 'tools': 0, 'actions': 0, 'model_calls': 0, 'input_tokens': 0, 'output_tokens': 0}
        self.lock = asyncio.Lock()
        self.wake = asyncio.Event()
        self.shutdown = asyncio.Event()

    def persist(self):
        from .plan_archive import bind_archive,prepare_archive,finish_archive
        bind_archive(self.current_plan,self.store,self.colony)
        snapshot,records,methods,evidence=prepare_archive(self.current_plan)
        self.store.archive_and_set(self.colony,'bridge:'+self.colony, {'chat': self.chat, 'plan': self.plan, 'projects': self.projects.dump(),
            'chat_revision': self.chat_revision, 'handled_revision': self.handled_revision,
            'draft_owners': self.draft_owners, 'current_plan': snapshot,
            'strategic_state': self.strategic_state.dump(), 'advice': self.advice},records,methods,evidence)
        finish_archive(self.current_plan,snapshot,records,methods,evidence)

    async def sync_identity(self):
        if self.game is None:
            raise ValueError('Wait for the native colony connection')
        identity = await self.game.query('home/colony_identity')
        key = identity['colonyId']+':'+str(identity['mapId'])
        token = key+':'+identity['loadToken']
        changed = token != self.context_token
        if changed:
            self.player_input = None
            self.ui_targets.clear()
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
            self.execution_window_end = None
            self.wake.clear()
            self.game.cinematic = False
            self.camera_path, self.camera_version = None, 0
            self.camera_bytes = None
            self.camera_error, self.camera_captured_at = '', 0
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
        if is_write(name, arguments) and 'dryRun' not in arguments:
            schema = await self.game.describe(name)
            if schema.get('properties', {}).get('dryRun', {}).get('type') == 'boolean':
                arguments = dict(arguments, dryRun=True)
        if is_write(name, arguments):
            raise PermissionError('Inspection cannot execute writes. Use dryRun=true for a supported preview, '
                                  'or commit a native operation for execution. Read describe for the native contract.')
        async with self.lock:
            if name == 'rimworld/get_ui_layout':
                if self.headless:
                    raise ValueError('UI layout needs a rendered game; use native state tools in no-graphics tests')
                self.ui_targets.clear()
                await self.bridge.call('home/render_demand', seconds=15)
                from .player_action_verification import selection_identity
                selection_before = selection_identity(await self.game.invoke('rimworld/get_selection_semantics', {}))
            result = await self.game.invoke(name, arguments, allow_write=False)
            if name == 'rimworld/get_ui_layout':
                from .player_action_verification import selection_identity
                selection = selection_identity(await self.game.invoke('rimworld/get_selection_semantics', {}))
                if selection_before != selection:
                    raise ValueError('Native selection changed during UI capture; inspect again')
                self.ui_targets = {e['targetId']: dict(e, load_token=self.context_token)
                    for s in result.get('surfaces', []) for e in s.get('elements', []) if e.get('targetId')}
                for target in self.ui_targets.values():
                    target['selection_identity'] = selection
                    target['direction_revision'] = self.chat_revision
            return result

    async def consult(self, role, question, sections, include_image=False, *, expected_token=None, expected_revision=None):
        from .config import ModelRole
        async with self.lock:
            await self.sync_identity()
            if expected_token != self.context_token or expected_revision != self.chat_revision:
                raise ValueError('Consultation context changed')
            selected = projection(self, sections)
            image = None
            if include_image:
                if ModelRole(role) != ModelRole.ARCHITECT:
                    raise ValueError('Only the architect accepts image context')
                image, source = await self._capture_visual_source()
                await self.sync_identity()
                if expected_token != self.context_token or expected_revision != self.chat_revision:
                    raise ValueError('Consultation context changed during capture')
                selected['image_source'] = source
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

    async def _capture_visual_source(self):
        """Capture under the runtime lock; callers recheck context after native awaits."""
        from .visual_source import image_url, retain_source
        if self.headless:
            raise ValueError('Visual review needs rendered mode; no image exists in headless mode')
        # Capture the player's current view without moving their camera.
        await self.bridge.call('home/render_demand', seconds=15)
        await asyncio.sleep(.3)
        before = (await self.bridge.call('rimworld/get_camera_state')).structuredContent
        keys = ('mapId', 'mapPosition', 'rootSize')
        if not isinstance(before, dict) or any(before.get(key) is None for key in keys):
            raise ValueError('Native camera provenance is unavailable')
        capture = await self.bridge.call('rimworld/take_screenshot',
            fileName='rimbot-review-'+uuid.uuid4().hex, includeTargets=False, suppressMessage=True)
        data = Path(capture.structuredContent['path']).read_bytes()
        after = (await self.bridge.call('rimworld/get_camera_state')).structuredContent
        if not isinstance(after, dict) or any(before[key] != after.get(key) for key in keys):
            raise ValueError('Player camera changed during capture; request a fresh review')
        status = await self.game.query('home/status', colonists=False, threats=False)
        source = {'load_token':self.context_token, 'tick':status['time']['ticksGame'],
            **retain_source(self.root, data), 'view':'current player camera', 'camera':before,
            'captured_at':time.time(), 'tick_note':'Read after screenshot; not an atomic state snapshot'}
        return image_url(data), source

    async def visual_review(self, question, focus=None, *, expected_token, expected_revision):
        from .visual_review import review
        from .visual_source import detail_frame, read_source
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
            image, source = await self._capture_visual_source()
            detail, bounds = detail_frame(read_source(self.root, source), focus)
            source['detail_bounds'] = bounds
            await check()
        result=await review(self.router,question,image,source,self.model_progress,detail)
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
            allocations = {}
            if decision.plan:
                validate_geometry(decision.plan, current=self.current_plan)
                await validate_native_steps(decision.plan, self.game)
                from .construction_cancellation import validate_cancellations
                if any(s.action.kind == 'cancel_construction' and not any(old.id == s.id for old in self.current_plan.spec.steps)
                       for s in decision.plan.steps):
                    from .construction_cancellation import validate_player_authorization
                    validate_player_authorization(self, expected_revision)
                await validate_cancellations(decision.plan, self.current_plan, self.game, self.identity)
                await preflight_construction(decision.plan, self.current_plan, self.game)
                allocations = await validate_allocations(decision.plan, self.current_plan, self.game)
                # Contract discovery can yield while the game loads another colony.
                await self.sync_identity()
                if expected_token != self.context_token or expected_revision != self.chat_revision:
                    raise ValueError('Colony or direction changed during validation; decision was not committed')
                validate_stand_down_steps(decision.plan, self.current_plan, self.draft_owners,
                    self.context_token, self.batch.summary.pawns)
            for identity in decision.used_consultations:
                if identity not in self.advice or self.advice[identity]['load_token'] != self.context_token:
                    raise ValueError('Consultation is missing or belongs to another loaded game')
            for step_id in decision.retry_steps:
                progress = self.current_plan.progress.get(step_id)
                if not progress or progress.state != 'blocked' or not progress.failure or not progress.failure.retryable:
                    raise ValueError('Step is not safely retryable: '+step_id)
            previous_ids = {step.id for step in self.current_plan.spec.steps}
            changed = self.current_plan.commit(decision, actor=actor, tick=self.batch.summary.end_tick)
            from .construction_cancellation import retire_sources
            retire_sources(self.current_plan, previous_ids)
            self.current_plan.control.setdefault('costs', {}).update(allocations)
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

    async def recover_medical(self, step_id, *, expected_token, expected_revision, limit):
        async with self.lock:
            await self.sync_identity()
            if self.mode != 'automate' or self.context_token != expected_token or self.chat_revision != expected_revision:
                return None
            plan = self.current_plan
            step = next((s for s in plan.spec.steps if s.id == step_id), None)
            if step is None: return None
            revision = plan.revision
            people = await self.game.query('home/list_pawns', colonistsOnly=True, health=True, work=True)
            status = await self.game.query('home/status', colonists=False, threats=False)
            await self.sync_identity()
            if (self.current_plan is not plan or plan.revision != revision or self.mode != 'automate'
                    or self.context_token != expected_token or self.chat_revision != expected_revision):
                return None
            tick = status.get('time', {}).get('ticksGame')
            if not isinstance(tick, int) or not isinstance(people.get('pawns'), list): return None
            before = plan.progress[step_id].model_dump()
            message = recover_treatment(plan, step, people['pawns'], token=expected_token, tick=tick,
                                        owners=self.draft_owners, limit=limit)
            if message:
                goal = plan.colony_goals[step.goal_id]
                changed = before != plan.progress[step_id].model_dump()
                goal.status = 'active' if plan.progress[step_id].state != 'blocked' else 'blocked'
                if goal.reason != message or changed:
                    goal.reason = message
                    goal.evidence['recovery'] = {'step': step_id, 'attempts': len(plan.progress[step_id].recovery_history),
                        'limit': limit, 'state': plan.progress[step_id].state, 'message': message}
                    if changed:
                        plan.revision += 1
                        goal.last_progress_tick = tick
                    self.note('treatment_recovery', message, step=step_id, changed=changed)
                    self.persist()
            return message

    def reconcile_plan(self):
        for step in self.current_plan.spec.steps:
            progress = self.current_plan.progress[step.id]
            if (isinstance(step.action, NativeOperation) and step.action.completion in ('pawn_equipped', 'pawn_at_position')
                    and progress.state == 'waiting' and self.batch):
                if self.batch.started_at <= progress.issued.get('0', {}).get('issued_at', float('inf')): continue
                outcome = pawn_order_outcome(step.action, self.batch.native.get('pawns', {}).get('pawns', []))
                if outcome != 'waiting':
                    progress.state = 'blocked' if isinstance(outcome, Failure) else 'complete'
                    progress.failure = outcome if isinstance(outcome, Failure) else None
                    self.signal('plan.step_'+progress.state, {'step': step.id})
                continue
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
                progress.failure = None
            elif row.state == 'blocked':
                # ProjectBook uses blocked for failed observations. This is not
                # evidence that issued construction disappeared or became illegal.
                progress.state = 'waiting'
                progress.failure = Failure(code='observation_unavailable',detail=row.evidence,retryable=True)
            elif row.state in ('planned', 'cancelled'):
                progress.state = 'blocked'
                progress.failure = Failure(code='plan_invalidated', detail=row.evidence)
            else:
                progress.state = 'waiting'
                progress.failure = None
            if old != progress.state:
                self.signal('plan.step_'+progress.state, {'step':step.id, 'evidence':row.evidence})

    async def cancel_plan_step(self, identity):
        async with self.lock:
            await self.sync_identity()
            self.current_plan.cancel(identity)
            step = next(s for s in self.current_plan.spec.steps if s.id==identity)
            goal = self.current_plan.colony_goals.get(step.goal_id) if step.goal_id else None
            if goal:
                goal.cancelled,goal.status,goal.reason = True,'blocked','Player cancelled an action in this goal'
                if goal.target.get('satisfies'):
                    self.current_plan.control.setdefault('suppressed_goals',{})[goal.target['satisfies']] = step.goal_id
            self.persist()
        await self.steer('Player cancelled plan step '+identity+'. Existing game orders are unchanged.',interpret=False)

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
            self.current_plan.control['player_direction']=self.current_plan.control.get('player_direction',0)+1
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

    async def steer(self, text, *, interpret=True):
        if getattr(self, 'session_closing', False): raise ValueError('Session is restarting; keep your draft and send it after reconnection')
        text = text.strip()
        if not text or len(text) > 4000:
            raise ValueError('Send a message between 1 and 4000 characters')
        self.chat_revision += 1
        self.current_plan.control['player_direction']=self.current_plan.control.get('player_direction',0)+1
        event = self.note('human' if interpret else 'control', text)
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
        self.execution_window_end = None
        self.execution_wait_explicit = False
        try:
            if self.supervisor:
                await self.supervisor.change('Paused')
            else:
                await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        except Exception as error:
            self.note('blocker', 'Could not pause the game: '+failure_text(error))
        await self.release_drafts()

    async def set_mode(self, mode, *, player_owner=None):
        if mode not in ('manual', 'automate'):
            raise ValueError('Choose Manual or Automate')
        async with self.lock:
            if not self.connected:
                raise ValueError('Wait for the bridge to connect')
            await self.sync_identity()
            if player_owner is not None:
                from .player_input import require_owner
                require_owner(self, *player_owner)
                direction = self.chat_revision
                if player_owner[0] != self.context_token:
                    raise ValueError('Loaded colony changed; player control was not released')
            elif mode == 'automate' and getattr(self, 'player_input', None) is not None:
                raise ValueError('Release player control before resuming automation')
            if mode == 'manual':
                await self.halt()
            if self.supervisor:
                await self.supervisor.change('Paused')
                # Only explicit player control clears an external pause latch.
                self.supervisor.allow_resume()
            else:
                await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            if player_owner is not None:
                await self.sync_identity()
                require_owner(self, *player_owner)
                if player_owner[0] != self.context_token or direction != self.chat_revision:
                    raise ValueError('Player direction changed during release; automation remains off')
            self.mode = mode
            if player_owner is not None:
                self.player_input = None
            self.resume_after_review = mode == 'automate'
        await self.steer('Control changed to '+mode+'. '+('Continue the colony plan.' if mode == 'automate' else 'Discuss and inspect only; do not issue game orders.'), interpret=False)

    async def control_clock(self, speed, *, mode='colony', ignored_hostiles='', ignored_downed='', expected_revision=None, expected_token=None, expected_plan_revision=None):
        async with self.lock:
            await self.sync_identity()
            await self.refresh_clock_events()
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed; clock was not changed')
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New direction arrived; clock was not changed')
            if expected_plan_revision is not None and expected_plan_revision != self.current_plan.revision:
                raise InterruptedError('Committed plan changed; clock was not changed')
            if self.mode != 'automate':
                raise ValueError('Automation is off; clock was not changed')
            if speed != 'Paused':
                dispatch_direction = self.chat_revision
                from .production_policy import sync_production_policy
                await sync_production_policy(self)
                await self.refresh_clock_events()
                if self.chat_revision != dispatch_direction:
                    raise InterruptedError('Native interruption arrived while preparing the clock; no resume sent')
            self.resume_after_review = False
            self.execution_window_end = None
            self.execution_wait_explicit = False
            result = await self.supervisor.change(speed, mode=mode,
                ignored_hostiles=ignored_hostiles, ignored_downed=ignored_downed,
                max_ticks=600 if speed != 'Paused' and expected_plan_revision is not None else None)
            if speed != 'Paused' and expected_plan_revision is not None and result.get('active'):
                self.execution_window_end = result['tickDeadline']
                self.execution_wait_explicit = True
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
            if kind == 'tick_budget':
                self.execution_window_end = None
                self.execution_wait_explicit = False
            if kind in HOLD_REASONS or kind == 'clock_error':
                self.current_plan.control['player_direction'] = self.current_plan.control.get('player_direction', 0) + 1
                self.mode = 'manual'
                self.phase = 'Clock held'
            # Deliver evidence to an in-flight planner, invalidate stale calls,
            # and wake an idle planner without claiming this is a player message.
            self.chat_revision += 1
            self.chat.append(dict(saved_event, revision=self.chat_revision))
            self.wake.set()
        if events:
            self.persist()

    async def refresh_clock_events(self):
        """Ingest native interruptions before a writer uses its captured direction.

        The background observer may have been waiting behind this writer lock.
        Buffered events and fresh native events must both invalidate old work.
        """
        poll = getattr(self.supervisor, 'poll', None)
        if poll is not None:
            self.clock_events.extend(await poll())
        self.receive_clock_events()

    async def native(self, name, arguments, *, expected_revision=None, expected_token=None, expected_plan_revision=None, reconcile=True, expected_step_id=None):
        if name == 'rimworld/set_time_speed':
            if set(arguments) - {'speed', 'ultraSpeedBoost'}:
                raise ValueError('Unknown time control argument')
            if arguments.get('ultraSpeedBoost'):
                raise ValueError('Use normal game speeds')
            return await self.control_clock(arguments.get('speed'), expected_revision=expected_revision, expected_token=expected_token, expected_plan_revision=expected_plan_revision)
        async with self.lock:
            await self.refresh_clock_events()
            if getattr(self, 'player_input', None) is not None and is_write(name, arguments):
                raise ValueError('Player control is held; no model or controller order was sent')
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived; this call was not executed')
            await self.sync_identity()
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed; no command sent')
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived; no command sent')
            if expected_plan_revision is not None and expected_plan_revision != self.current_plan.revision:
                raise InterruptedError('Committed plan changed; no order sent')
            if is_write(name, arguments) and name not in ('rimworld/click_ui_target', 'rimworld/scroll_ui_target'):
                self.ui_targets.clear()
            if name == 'rimworld/close_main_tab' and not arguments.get('mainTabId'):
                raise ValueError('Specify the inspected mainTabId to close')
            if name == 'rimworld/open_letter':
                if self.mode != 'automate' or not self.supervisor:
                    raise ValueError('Enable Automate before opening a letter')
                self.resume_after_review = False
                from .dialog_control import require_clear_windows
                require_clear_windows(await self.game.invoke('rimworld/get_ui_state', {}))
                # Deliberately stop the owned lease before a letter forces pause.
                # Do not clear player holds or resume on window disappearance.
                await self.supervisor.pause_for_dialog()
                await self.sync_identity()
            if name in ('rimworld/click_ui_target', 'rimworld/scroll_ui_target'):
                target = self.ui_targets.get(arguments.get('targetId'))
                if not target or target.get('load_token') != self.context_token:
                    raise ValueError('UI target was not observed in this load; inspect get_ui_layout again')
                if name == 'rimworld/click_ui_target' and (not target.get('actionable') or target.get('disabled') is True):
                    raise ValueError('Observed UI control is not enabled and actionable')
                if name == 'rimworld/scroll_ui_target' and target.get('kind') != 'scroll_view':
                    raise ValueError('Select an observed scroll_view target')
                if self.headless:
                    raise ValueError('UI control needs a rendered game')
                from .player_action_verification import selection_identity
                if target.get('direction_revision') != self.chat_revision:
                    self.ui_targets.clear()
                    raise ValueError('Player direction changed; inspect the UI again')
                current_selection = selection_identity(await self.game.invoke('rimworld/get_selection_semantics', {}))
                if target.get('selection_identity') != current_selection:
                    self.ui_targets.clear()
                    raise ValueError('Native selection changed; inspect the UI again')
                await self.bridge.call('home/render_demand', seconds=15)
                # A failed or uncertain click must not be replayed from this capture.
                self.ui_targets.clear()
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
            dismissed_window = None
            if name == 'rimworld/click_screen_target':
                from .dialog_control import dismissal_target
                dismissed_window = dismissal_target(arguments.get('targetId'),
                    await self.game.invoke('rimworld/get_screen_targets', {}))
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('New player direction arrived during preparation; no command sent')
            if expected_token is not None and expected_token != self.context_token:
                raise ValueError('Loaded colony changed during preparation; no command sent')
            hunting_target=None
            if expected_step_id and name=='rimworld/apply_architect_designator':
                step=next((s for s in self.current_plan.spec.steps if s.id==expected_step_id),None)
                if step and step.goal_id=='EnsureFoodSupply' and '-hunt-' in step.id:
                    from .hunting import HuntingRefused,validate_hunt
                    goal=self.current_plan.colony_goals.get(step.goal_id)
                    target=goal.evidence.get('hunting_targets',{}).get(step.id) if goal else None
                    if not target or target.get('signature')!=step.signature() or arguments!=step.action.arguments:
                        raise HuntingRefused('Hunting intent is missing or changed; no designation sent')
                    status=await self.game.query('home/status',colonists=False,threats=False)
                    if status.get('time',{}).get('paused') is not True or type(status.get('time',{}).get('ticksGame')) is not int:
                        raise HuntingRefused('Hunting validation requires a paused game; no designation sent')
                    evidence=await validate_hunt(self.game,arguments,target)
                    await self.sync_identity()
                    after=await self.game.query('home/status',colonists=False,threats=False)
                    if (self.context_token!=expected_token or self.chat_revision!=expected_revision
                            or self.current_plan.revision!=expected_plan_revision
                            or after.get('time',{}).get('paused') is not True
                            or after.get('time',{}).get('ticksGame')!=status.get('time',{}).get('ticksGame')):
                        raise HuntingRefused('Colony, direction or tick changed during hunting validation; no designation sent')
                    goal.evidence['hunting_dispatch']=evidence
                    hunting_target=target['prey']
            explicit = (self.manual_execution is not None and self.manual_execution ==
                        (expected_token, expected_revision, expected_plan_revision) ==
                        (self.context_token, self.chat_revision, self.current_plan.revision))
            if name == 'home/bills' and not arguments.get('dryRun', True) and (self.mode == 'automate' or explicit):
                from .production_policy import sync_production_policy
                await sync_production_policy(self)
            await self.refresh_clock_events()
            if expected_revision is not None and expected_revision != self.chat_revision:
                raise ValueError('Native interruption arrived during preparation; no command sent')
            result = await self.game.invoke(name, arguments, allow_write=self.mode == 'automate' or explicit)
            if hunting_target:
                observed=await self.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
                prey=next((p for p in observed.get('pawns',[]) if p.get('thingId')==hunting_target),None)
                if not prey or (prey.get('animals') or {}).get('designations',{}).get('hunt') is not True:
                    raise ValueError('Hunting write was not confirmed on the selected animal; inspect before retrying')
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
                    verification = await self.game.query('home/list_zones', includeCells=True, maxCellsPerZone=100000, filter=True)
                    from .player_action_verification import verify_zone_edit
                    verify_zone_edit(arguments, result, verification)
                elif name == 'home/bills' and arguments.get('only'):
                    verification = await self.game.query('home/bills', action='list', bench=arguments.get('bench'), dryRun=True)
                    from .player_action_verification import verify_bill_whitelist
                    verify_bill_whitelist(result, verification)
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
                elif name == 'rimworld/open_letter':
                    verification = await self.game.invoke('rimworld/get_ui_state', {})
                    from .dialog_control import verify_letter_window
                    verify_letter_window(verification)
                elif name in ('rimworld/click_ui_target', 'rimworld/scroll_ui_target',
                              'rimworld/open_main_tab', 'rimworld/close_main_tab'):
                    if result.get('success') is not True:
                        raise ValueError(result.get('message') or 'Native UI action was not confirmed')
                    verification = await self.game.invoke('rimworld/get_ui_state', {})
                    if verification.get('success') is not True:
                        raise ValueError('UI state readback is unavailable')
                    if name in ('rimworld/click_ui_target', 'rimworld/scroll_ui_target'):
                        from .player_action_verification import selection_identity
                        selection = await self.game.invoke('rimworld/get_selection_semantics', {})
                        selection_identity(selection)
                        verification = dict(verification, selection=selection)
                    if name == 'rimworld/close_main_tab':
                        from .player_action_verification import verify_main_tab_closed
                        verify_main_tab_closed(arguments, verification)
                    if name == 'rimworld/open_main_tab':
                        opened = result.get('after', {}).get('openMainTabId')
                        if not opened or verification.get('openMainTabId') != opened:
                            raise ValueError('Main tab opening was not confirmed')
                elif name == 'rimworld/click_screen_target':
                    verification = await self.game.invoke('rimworld/get_ui_state', {})
                    from .dialog_control import verify_window_dismissal
                    verify_window_dismissal(arguments['targetId'], dismissed_window, result, verification)
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
        self.deliberating = True
        try:
            self.phase = 'Inspecting colony'
            async with self.lock:
                await self.sync_identity()
                if self.supervisor:
                    await self.supervisor.change('Paused')
                self.execution_window_end = None
                self.execution_wait_explicit = False
                self.resume_after_review = self.mode == 'automate'
                self.batch = await observe(self.game)
                self.update_strategy_state()
                await self.projects.reconcile(self.game)
                self.reconcile_plan()
                self.persist()
            # Inference is requested only by a new player message. Native events
            # and normal operation always go through the deterministic controller.
            player_revision = max((m.get('revision', 0) for m in self.chat if m.get('kind') == 'human'), default=0)
            if player_revision > self.current_plan.control.get('interpreted_player_revision', 0):
                chat_token = self.context_token
                try:
                    await self.planner.play_bridge()
                except Exception as error:
                    self.reply('Chat request could not be interpreted: '+failure_text(error)+'. Autopilot can continue.')
                if self.context_token != chat_token: return
                self.current_plan.control['interpreted_player_revision'] = player_revision
            await self.controller.cycle()
            self._long_event_deadline = None
            await self.execute_manual_requests()
            self.phase = 'Executing orders' if self.mode == 'automate' else 'Manual'
        except asyncio.CancelledError:
            raise
        except Exception as error:
            if self.defer_long_event(error): return
            self.phase = 'Needs attention'
            self.reply('Review stopped: '+failure_text(error))
            async with self.lock:
                await self.halt()
            self.handled_revision = self.chat_revision
        finally:
            self.deliberating = False
            pending_player = max((m.get('revision', 0) for m in self.chat if m.get('kind') == 'human'), default=0)
            if (self.chat_revision > self.handled_revision
                    or pending_player > self.current_plan.control.get('interpreted_player_revision', 0)):
                self.wake.set()

    async def advance_execution(self):
        """Issue orders while paused; only confirmed pending work starts a window."""
        try:
            await self._advance_execution()
            self._long_event_deadline = None
        except asyncio.CancelledError:
            raise
        except Exception as error:
            if self.defer_long_event(error): return
            self.reply('Execution stopped: '+failure_text(error))
            async with self.lock:
                await self.halt()

    async def execute_manual_requests(self):
        """A current explicit chat order can dispatch while autopilot stays off."""
        requests, self.manual_requests = self.manual_requests, []
        if self.mode != 'manual' or getattr(self, 'player_input', None) is not None: return
        ids = {identity for identity, token, direction in requests
               if token == self.context_token and direction == self.chat_revision}
        if not ids: return
        self.manual_execution = (self.context_token,self.chat_revision,self.current_plan.revision)
        try:
            # A semantic shell is bounded by the plan schema. Time never resumes
            # here; native construction/movement remains observed waiting work.
            remaining, budget = set(ids), 512
            while remaining and budget > 0:
                ready = {step.id for step in self.current_plan.ready() if step.id in remaining}
                if not ready:
                    break
                before = sum(len(self.current_plan.progress[identity].issued) for identity in ready)
                await self.hands.advance(self, max_operations=budget, only_ids=ready)
                after = sum(len(self.current_plan.progress[identity].issued) for identity in ready)
                budget -= max(1, after-before)
                remaining -= ready
                if self.manual_execution != (self.context_token,self.chat_revision,self.current_plan.revision):
                    break
        finally:
            self.manual_execution = None

    def defer_long_event(self, error):
        # This exact native refusal occurs before dispatch. Re-observe through
        # the normal review path; never replay the failed write here.
        if 'A long event (autosave, map generation) is running or queued' not in failure_text(error):
            self._long_event_deadline = None
            return False
        deadline = getattr(self, '_long_event_deadline', None)
        if deadline is None:
            self._long_event_deadline = time.monotonic() + 20
        elif time.monotonic() >= deadline:
            return False
        self.phase = 'Waiting for native long event'
        self.resume_after_review = self.mode == 'automate'
        self.wake.set()
        return True

    async def _advance_execution(self):
        if self.mode != 'automate' or self.deliberating or self.wake.is_set():
            return
        await self.hands.advance(self)
        async with self.lock:
            await self.sync_identity()
            if (self.mode != 'automate' or self.deliberating or self.wake.is_set()
                    or self.chat_revision > self.handled_revision or not self.supervisor
                    or self.current_plan.control.get('execution_hold')):
                return
            waiting = [p for s in self.current_plan.spec.steps
                       if (p := self.current_plan.progress[s.id]).state == 'waiting'
                       and any(v.get('confirmed') for v in p.issued.values())]
            ongoing = self.current_plan.control.get('simulation_needed', False)
            if self.resume_after_review:
                self.resume_after_review = False
                if (waiting or ongoing) and not self.supervisor.hold:
                    token, direction = self.context_token, self.chat_revision
                    from .production_policy import sync_production_policy
                    await sync_production_policy(self)
                    status = await self.game.query('home/status', colonists=False, threats=False)
                    await self.sync_identity()
                    await self.refresh_clock_events()
                    if (self.mode != 'automate' or self.context_token != token
                            or self.chat_revision != direction or self.wake.is_set()):
                        return
                    combat = self.current_plan.control.get('combat',{})
                    fighting = self.current_plan.colony_goals.get('ActiveCombat')
                    engaged = bool(fighting and fighting.status=='active' and not fighting.cancelled
                        and combat.get('steps') and all(s in self.current_plan.progress
                            and self.current_plan.progress[s].state=='complete' for s in combat['steps']))
                    if engaged:
                        from .combat_health import combat_health_hold
                        people = await self.game.query('home/list_pawns', colonistsOnly=True, health=True)
                        await self.sync_identity()
                        await self.refresh_clock_events()
                        if (self.mode != 'automate' or self.context_token != token
                                or self.chat_revision != direction or self.wake.is_set()):
                            return
                        hold = combat_health_hold(people)
                        if hold:
                            self.current_plan.control['execution_hold'] = hold
                            fighting.status, fighting.reason = 'blocked', hold
                            self.note('combat_health_hold', hold)
                            self.persist()
                            return
                    ticks=600 if waiting or engaged else 3000
                    clock = await self.supervisor.change('Normal' if engaged else self.controller.policy.execution_speed,
                        mode='combat' if engaged else 'colony',
                        ignored_hostiles=combat['target'] if engaged else '', max_ticks=ticks)
                    self.execution_window_end = clock['tickDeadline'] if clock.get('active') else None
                    self.execution_wait_explicit = False
                    self.note('execution_window', f'Native work: at most {ticks} game ticks before review', clock=clock)
            if self.execution_window_end is not None:
                status = await self.game.query('home/status', colonists=False, threats=False)
                if (not waiting and not ongoing and not self.execution_wait_explicit
                        or status['time']['ticksGame'] >= self.execution_window_end):
                    await self.supervisor.change('Paused')
                    self.execution_window_end = None
                    self.signal('execution.window_finished', {'tick': status['time']['ticksGame'],
                                                             'work_remaining': bool(waiting)})

    async def run(self):
        try:
            executable = gabs_executable(self.root)
            from .headless import prepare
            configuration = prepare(self.root) if self.headless else self.root/'config'
            checkpoint = None
            if self.resume:
                from .session_checkpoint import install_saved_game
                checkpoint = install_saved_game(self.resume)
            async with bridge_session(executable, configuration) as bridge:
                self.bridge = bridge
                if self.fresh:
                    await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                if self.resume:
                    await bridge.call('rimworld/load_game_ready', saveName=checkpoint['save_name'], readiness='visual', timeoutMs=90000, ignoreModCompatibility=False)
                elif self.fresh:
                    await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000, ignoreModCompatibility=self.headless or rendered_headless_mismatch(self.root))
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                self.game = BridgeGame(bridge)
                await self.sync_identity()
                if checkpoint:
                    status = await self.game.query('home/status', colonists=False, threats=False)
                    if (self.identity['colonyId'] != checkpoint['colony_id'] or self.identity['mapId'] != checkpoint['map_id']
                            or status.get('time', {}).get('ticksGame') not in (checkpoint['tick'], checkpoint['tick'] + 1)):
                        raise ValueError(f"Resumed native colony does not match checkpoint: expected {checkpoint['colony_id']}:{checkpoint['map_id']} at {checkpoint['tick']}, observed {self.colony} at {status.get('time', {}).get('ticksGame')}; automation remains off")
                    self.note('checkpoint_resumed', 'Checkpoint restored in Manual.',
                              saved_tick=checkpoint['tick'], loaded_tick=status['time']['ticksGame'])
                await self.projects.reconcile(self.game)
                self.persist()
                self.batch = await observe(self.game)
                self.connected, self.phase = True, 'Manual'
                self.clock_task = asyncio.create_task(self.watch_clock())
                self.update_strategy_state()
                last_reconcile = 0
                while not self.stopped:
                    if ((self.review_task is None or self.review_task.done())
                            and (self.execution_task is None or self.execution_task.done())):
                        if self.wake.is_set():
                            self.wake.clear()
                            self.review_task = asyncio.create_task(self.review())
                    if (self.mode == 'automate' and self.handled_revision >= self.chat_revision
                            and (self.review_task is None or self.review_task.done())
                            and not self.wake.is_set()
                            and (self.execution_task is None or self.execution_task.done())):
                        self.execution_task = asyncio.create_task(self.advance_execution())
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
                            snapshot_watching = any(until > time.monotonic() and viewer not in self.streaming_viewers
                                                    for viewer, until in self.video_viewers.items())
                            if not self.headless and snapshot_watching:
                                await asyncio.sleep(.15)
                                image = await bridge.call('rimworld/take_screenshot', fileName='rimbot-live', includeTargets=False, suppressMessage=True)
                                candidate = Path(image.structuredContent['path']).resolve()
                                if candidate.is_relative_to(self.root) and candidate.is_file():
                                    frame = candidate.read_bytes()
                                    if not frame.startswith(b'\x89PNG\r\n\x1a\n'):
                                        raise ValueError('Camera returned an incomplete frame')
                                    self.camera_bytes = frame
                                    self.camera_path = candidate
                                    self.camera_version += 1
                                    self.camera_captured_at = time.time()
                                    self.camera_error = ''
                                else:
                                    raise ValueError('Camera result is unavailable in this session')
                    except Exception as error:
                        self.camera_error = str(error)[:300]
                        self.note('camera_error', str(error))
                    try:
                        await asyncio.wait_for(self.shutdown.wait(), 2)
                    except TimeoutError:
                        pass
                async with self.lock:
                    if not getattr(self, 'owned_game_stopped', False): await self.halt()
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
        return {'autopilotSettings': settings_state(self.current_plan), 'chatModel': self.router.routing.roles[ModelRole.STRATEGIST].model, 'memories': self.public_memories(), 'sessionId': self.context_token or self.colony, 'projects': self.projects.dump(), 'goals': self.plan, 'mood': 'thinking' if self.review_task and not self.review_task.done() else 'happy',
            'status': {'phase': 'core', 'turn': self.counters['model_calls'],
                       'label': self.phase},
            'game': {'tick': self.clock.get('ticksGame', summary.end_tick if summary else None), 'paused': self.clock.get('paused', True),
                     'stale': not self.connected, 'wallTs': time.time()},
            'lastSummary': recent, 'feed': feed, 'mode': self.mode, 'connected': self.connected,
            'visualReviews': [dict(id=a['id'], question=a['question'], source=a['source'], report=a['report'],
                historical=True, current_load=a.get('load_token') == self.context_token)
                for a in reversed(list(self.advice.values())) if a.get('source')][:8],
            'currentPlan': {'revision': self.current_plan.revision, 'chosen_tick': self.current_plan.chosen_tick,
                'controller': self.current_plan.control,
                'colonyGoals': {k: v.model_dump() for k, v in self.current_plan.colony_goals.items()},
                'rationale': self.current_plan.rationale, 'goals': self.current_plan.spec.goals,
                'constraints': self.current_plan.spec.constraints, 'risks': self.current_plan.spec.risks,
                'steps': [dict(id=s.id, title=s.title, priority=s.priority, action=s.action.kind,
                    source=s.source, goal_id=s.goal_id,
                    completion=s.completion_criteria, state=self.current_plan.progress[s.id].state,
                    issued=len(self.current_plan.progress[s.id].issued),
                    failure=self.current_plan.progress[s.id].failure.model_dump() if self.current_plan.progress[s.id].failure else None)
                    for s in self.current_plan.spec.steps]}, 'modelRoles': self.router.metrics,
            'cinematic': getattr(self.game, 'cinematic', False),
            'clockSupervisor': self.supervisor.state if self.supervisor else {},
            'rendering': self.render_state, 'headless': self.headless, 'cameraError': self.camera_error, 'cameraCapturedAt': self.camera_captured_at, 'cameraVersion': self.camera_version, 'counters': self.counters,
            'observation': summary.model_dump() if summary else None}

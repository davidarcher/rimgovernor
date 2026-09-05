import asyncio
import hashlib
import json
import time
import uuid
from .catalog import Catalog
from .config import Settings
from .contracts import Action, Check, Query, Plans, Proposal, DailyPlan, select, satisfies
from .model import LocalModel, ModelError
from .planner import Planner, ROLES
from .rimapi import RimAPI, APIError, snapshot, compact


class Runtime:
    def __init__(self, store, settings=None, *, api_factory=RimAPI, model_factory=LocalModel):
        self.store = store
        self.settings = settings or Settings.model_validate(store.get('settings', {}))
        self.catalog = Catalog()
        self.api_factory, self.model_factory = api_factory, model_factory
        self.api = api_factory(self.settings.rimapi_url, self.catalog)
        self.model = model_factory(self.settings)
        self.planner = Planner(self)
        self.mode = 'manual'
        self.connected = False
        self.colony = ''
        self.observation = {}
        self.memory = store.get('inbox', self.empty_memory())
        self.status = {'phase':'Connecting', 'detail':'Waiting for RIMAPI'}
        self.counters = {'tools':0, 'actions':0, 'input_tokens':0, 'output_tokens':0, 'model_calls':0}
        self.generation = 0
        self.cycle_generation = 0
        self.task = None
        self.executing = False
        self.listeners = set()
        self.background = []
        self.events_pending = []
        self.last_tick = None
        self.last_review = -100000
        self.last_review_wall = 0
        self.poll_lock = asyncio.Lock()
        self.started_at = None
        self.event_connection = False
        self.stopped = False
        self.steering_pending = False

    @staticmethod
    def empty_memory():
        return {'direction':[], 'plans':None, 'goals':[], 'work':[], 'chat':[], 'last_plan_day':None, 'last_daily_day':None}

    def persist(self):
        if self.colony:
            self.store.set('colony:'+self.colony, self.memory)
        else:
            self.store.set('inbox', self.memory)

    def note(self, kind, text, **extra):
        event = self.store.event(self.colony, kind, text=text, **extra)
        for q in list(self.listeners):
            if q.full():
                q.get_nowait()
            q.put_nowait(event)
        return event

    async def progress(self, **values):
        self.status.update(values)
        self.note('progress', self.status.get('detail',''), **values)

    async def model_progress(self, values):
        self.check_generation()
        await self.progress(**values)

    def usage(self, usage):
        self.counters['model_calls'] += 1
        self.counters['input_tokens'] += usage.get('prompt_tokens', 0)
        self.counters['output_tokens'] += usage.get('completion_tokens', 0)

    def check_generation(self):
        if self.cycle_generation != self.generation:
            raise asyncio.CancelledError()

    def busy(self):
        return self.task is not None and not self.task.done()

    def public(self):
        return {'mode':self.mode, 'connected':self.connected, 'colony':self.colony,
                'busy':self.busy(), 'status':self.status, 'started_at':self.started_at,
                'counters':self.counters, 'settings':self.settings.model_dump(),
                'observation':compact(self.observation, 100000), 'memory':self.memory,
                'activity':self.store.history(self.colony,80), 'events_connected':self.event_connection,
                'capabilities':len(self.catalog.listing()), 'catalog_revision':self.catalog.revision}

    async def start(self):
        self.background = [asyncio.create_task(self.poll_loop()), asyncio.create_task(self.event_loop())]

    async def stop(self):
        self.stopped = True
        await self.cancel()
        for task in self.background:
            task.cancel()
        await asyncio.gather(*self.background, return_exceptions=True)
        await self.api.close()
        await self.model.close()

    async def cancel(self):
        self.generation += 1
        if self.busy() and not self.executing:
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)

    async def configure(self, settings):
        self.mode = 'manual'
        await self.cancel()
        if self.executing:
            raise ValueError('An action is finishing; retry settings when it completes.')
        async with self.poll_lock:
            await self.api.close()
            await self.model.close()
            self.settings = settings
            self.catalog = Catalog()
            self.api = self.api_factory(settings.rimapi_url, self.catalog)
            self.model = self.model_factory(settings)
            self.store.set('settings', settings.model_dump())
            self.connected = False
            self.last_tick = None
        self.note('settings', 'Connection settings updated.')

    async def poll(self):
        async with self.poll_lock:
            if not self.catalog.discovered:
                await self.api.discover()
            observed = await snapshot(self.api, self.settings.map_id)
            m = observed['map']
            session = observed['game'].get('session_id')
            if not session:
                raise APIError('Update RIMAPI to the RimBot build with game session identity.')
            identity = hashlib.sha256(json.dumps([self.settings.rimapi_url, session, m['id']]).encode()).hexdigest()[:16]
            tick = observed['game'].get('game_tick',0)
            if identity != self.colony:
                inbox = self.memory if not self.colony else self.empty_memory()
                if self.colony:
                    self.mode = 'manual'
                    await self.cancel()
                self.colony = identity
                self.memory = self.store.get('colony:'+identity, inbox)
                self.store.set('inbox', self.empty_memory())
                self.last_tick = None
                self.last_review = -100000
                self.last_review_wall = 0
                self.events_pending = []
                self.steering_pending = False
                self.started_at = None
                self.counters = dict.fromkeys(self.counters, 0)
                self.status = {'phase':'Manual', 'detail':'Colony connected'}
                self.api.invalidate(definitions=True)
                self.note('connection', 'Colony connected. Ready for your direction.')
            if self.last_tick is not None and tick < self.last_tick:
                self.mode = 'manual'
                await self.cancel()
                # Loading an earlier save invalidates pending actions, not player goals.
                self.memory['work'] = []
                self.api.invalidate(definitions=True)
                self.note('connection', 'Earlier save loaded. Work tracking refreshed; control is Manual.')
            self.last_tick = tick
            self.observation = observed
            self.connected = True
            if not self.busy() and self.status.get('phase')!='Needs attention':
                self.status = {'phase':'Watching' if self.mode == 'automate' else 'Manual', 'detail':'Colony connected'}
            await self.reconcile()

    async def poll_loop(self):
        while True:
            try:
                await self.poll()
                tick = self.observation['game'].get('game_tick',0)
                changed = bool(self.events_pending) and time.monotonic()-self.last_review_wall > 15
                due = tick-self.last_review >= self.settings.review_ticks
                if self.mode == 'automate' and not self.busy() and not self.observation['game'].get('is_paused') and (due or changed):
                    self.launch_review()
                elif self.steering_pending and not self.busy():
                    self.steering_pending = False
                    self.launch_review(steering=True)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                was_connected = self.connected
                self.connected = False
                self.event_connection = False
                self.status = {'phase':'Disconnected', 'detail':str(e)[:400]}
                if was_connected:
                    await self.cancel()
                    self.note('error', str(e)[:400])
            await asyncio.sleep(self.settings.poll_seconds)

    async def event_loop(self):
        # RLE's MIT SSE client supplies reconnecting transport and event parsing.
        from .vendor.sse_client import RimAPISSEClient
        while True:
            url = self.settings.rimapi_url
            client = RimAPISSEClient(url)
            listen = asyncio.create_task(client.listen())
            try:
                while url == self.settings.rimapi_url:
                    await asyncio.sleep(1)
                    for e in client.drain():
                        self.event_connection = True
                        self.api.invalidate()
                        if e.event_type in ('heartbeat','game_state','connected','log_message'):
                            continue
                        self.events_pending.append({'type':e.event_type,'data':e.data})
                        self.events_pending = self.events_pending[-40:]
                        text = str(e.data.get('label') or e.data.get('text') or e.event_type)[:250]
                        self.note('notification',text,event_type=e.event_type,data=compact(e.data,3000))
            finally:
                self.event_connection = False
                client.stop()
                listen.cancel()
                await asyncio.gather(listen, return_exceptions=True)

    async def set_mode(self, mode):
        if mode not in ('manual','automate'):
            raise ValueError('Choose Manual or Automate.')
        if mode == 'automate' and not self.connected:
            raise ValueError('Connect to a loaded colony first.')
        self.mode = mode
        self.steering_pending = False
        await self.cancel()
        self.note('control', 'Colony automation enabled.' if mode=='automate' else 'You have control. Automation stopped.')
        if mode == 'automate' and not self.busy():
            self.launch_review()

    async def steer(self, text):
        text = text.strip()
        if not text or len(text)>3000:
            raise ValueError('Enter a direction of 1–3000 characters.')
        self.memory['direction'].append(text)
        self.memory['direction'] = self.memory['direction'][-12:]
        self.memory['chat'].append({'role':'player','text':text,'at':time.time()})
        self.persist()
        self.note('steering',text)
        self.steering_pending = True
        await self.cancel()
        if self.connected and not self.busy():
            self.steering_pending = False
            self.launch_review(steering=True)
        elif not self.connected:
            self.note('info','Direction saved. Connect a colony to get a response.')

    def launch_review(self, steering=False, strategy=False):
        if self.busy():
            raise ValueError('A review is already running.')
        if not self.connected:
            raise ValueError('Connect to RIMAPI first.')
        self.task = asyncio.create_task(self.review(steering, strategy))

    def normalize_query(self, q: Query):
        # Local models sometimes nest our read filters inside native arguments.
        # Move only unambiguous wrapper fields; never steal a native argument.
        props = self.catalog.get(q.endpoint, False)['schema'].get('properties', {})
        result = q.model_copy(deep=True)
        defaults = Query(endpoint=q.endpoint)
        for key in ('path','where','search','fields','sort_by','near','offset','limit'):
            if key not in result.arguments or key in props:
                continue
            value = result.arguments[key]
            if getattr(result,key) != getattr(defaults,key) and getattr(result,key) != value:
                raise ValueError(f'Conflicting {key} filters beside and inside arguments.')
            setattr(result,key,result.arguments.pop(key))
        return Query.model_validate(result.model_dump())

    async def query(self, q: Query):
        q = self.normalize_query(q)
        e = self.catalog.get(q.endpoint, False)
        args = dict(q.arguments)
        if 'map_id' in e['schema'].get('properties', {}):
            current = self.observation.get('map',{}).get('id')
            args.setdefault('map_id',current)
            if args['map_id'] != current:
                raise ValueError('Query must target the selected map.')
        raw = await self.api.call(q.endpoint, args, write=False)
        return select(raw, q)

    def validate_check(self, check):
        check.query = self.normalize_query(check.query)
        e = self.catalog.get(check.query.endpoint, False)
        args = dict(check.query.arguments)
        if 'map_id' in e['schema'].get('properties',{}):
            args.setdefault('map_id',self.observation['map']['id'])
        self.catalog.validate(check.query.endpoint,args,False)

    async def check(self, check):
        return satisfies(await self.query(check.query), check)

    async def reconcile(self):
        changed = False
        for work in self.memory['work']:
            if work['status'] not in ('issued','unknown','waiting'):
                continue
            try:
                done = await self.check(Check.model_validate(work['action']['done']))
                if done:
                    work['status'] = 'complete'
                    work['detail'] = 'Verified in the colony'
                    changed = True
                # Unknown/pending observations never block replacement actions.
                # Expire tracking after a day; the next review inspects live state.
                elif self.last_tick - work.get('tick',self.last_tick) > 60000:
                    work['status'] = 'unresolved'
                    work['detail'] = 'Not observed after a day; needs a fresh assessment'
                    changed = True
            except (ValueError, APIError):
                work['detail'] = 'Waiting for a fresh observation'
        if changed:
            self.persist()
            self.note('work','Work updated from colony observations.')

    async def execute(self, action: Action, role):
        self.check_generation()
        if self.mode != 'automate':
            return
        e = self.catalog.validate(action.endpoint, action.arguments, True)
        await self.validate_build_materials(action)
        if 'map_id' in action.arguments and action.arguments['map_id'] != self.observation['map']['id']:
            raise ValueError('Action targets a different map.')
        if await self.check(action.done):
            self.note('action',f'Already done: {action.title}',role=role)
            return
        for requirement in action.requires:
            if not await self.check(requirement):
                raise ValueError(f'Prerequisite changed: {action.title}')
        # Only in-flight duplicates are suppressed; saved work never owns a site.
        work = {'id':uuid.uuid4().hex[:12], 'title':action.title,'status':'unknown',
                'detail':'Sending order', 'role':role,'action':action.model_dump(), 'tick':self.last_tick}
        self.memory['work'].append(work)
        self.memory['work'] = self.memory['work'][-160:]
        self.persist()  # Unknown before sending: restart cannot imply success.
        self.executing = True
        try:
            self.check_generation()
            # Do not silently unpause or issue new game orders through a player pause.
            game = await self.api.call('get_game_state',{},fresh=True)
            if game.get('session_id') != self.observation.get('game',{}).get('session_id'):
                work['status'] = 'cancelled'
                work['detail'] = 'Colony changed before this order was sent'
                self.mode = 'manual'
                return
            if game.get('is_paused'):
                work['status'] = 'deferred'
                work['detail'] = 'Game paused before this order was sent'
                return
            result = await self.api.call(action.endpoint, action.arguments, write=True)
            work['status'] = 'issued'
            work['detail'] = 'Order issued; checking the colony'
            self.counters['actions'] += 1
            self.note('action',action.title,role=role,endpoint=e['path'],arguments=action.arguments,result=compact(result,5000))
            if await self.check(action.done):
                work['status'], work['detail'] = 'complete', 'Verified in the colony'
        except asyncio.CancelledError:
            work['detail'] = 'Interrupted; outcome will be checked before any repeat'
            raise
        except Exception as e:
            work['detail'] = str(e)[:500]
            self.note('error',f'{action.title}: {e}',role=role)
            # A transport error may have happened after the game applied it.
            # No automatic retry here; reconciliation checks actual state.
            raise
        finally:
            self.executing = False
            self.persist()

    async def validate_build_materials(self, action):
        if action.endpoint != 'post_builder_blueprint':
            return
        definitions = (await self.api.call('get_def_all', {})).get('things_defs', [])
        by_name = {d['def_name']:d for d in definitions}
        for building in action.arguments.get('blueprint', {}).get('buildings', []):
            name = building.get('def_name')
            definition = by_name.get(name)
            if definition is None:
                raise ValueError(f'Unknown building definition: {name}')
            stuff = building.get('stuff_def_name')
            if definition.get('made_from_stuff') and stuff not in definition.get('allowed_stuff_defs', []):
                raise ValueError(f'{name} requires stuff_def_name. Choose from its native allowed_stuff_defs: '+', '.join(definition.get('allowed_stuff_defs', [])))
            if definition.get('made_from_stuff') is False and stuff:
                raise ValueError(f'{name} does not use stuff_def_name.')

    async def coordinate(self, context, roles):
        """Shared manager/administrator path for normal reviews and live tests."""
        proposals = await self.planner.proposals(context,[r for r in roles if r!='Workforce'])
        preliminary = None
        if any(p['labor'] for p in proposals.values()):
            preliminary = await self.planner.arbitrate(context,proposals)
            context['approved_labor'] = {r:proposals[r]['labor'] for r in preliminary.accepted}
            context['deferred_labor'] = preliminary.deferred
        if 'Workforce' in roles or preliminary:
            proposals.update(await self.planner.proposals({**context,'other_managers':proposals},['Workforce']))
        self.check_generation()
        decision = await self.planner.arbitrate(context,proposals)
        if preliminary and set(decision.accepted)-{'Workforce'}-set(preliminary.accepted):
            raise ModelError('Final decision granted new labor after Workforce review. No orders sent.')
        return proposals,decision

    async def review(self, steering=False, strategy=False):
        self.cycle_generation = self.generation
        self.started_at = time.time()
        self.counters = {k:0 for k in self.counters}
        events = self.events_pending
        self.events_pending = []
        self.last_review_wall = time.monotonic()
        self.last_review = self.last_tick or 0
        try:
            context = {'colony':compact(self.observation,20000),'player_direction':self.memory['direction'],
                       'goals':self.memory['goals'],'plans':self.memory['plans'],
                       'work':[{k:v for k,v in w.items() if k!='action'} for w in self.memory['work'][-30:]],
                       'notifications':compact(events,6000), 'mode':self.mode,
                       'capabilities':self.catalog.listing()}
            day = (self.last_tick or 0)//60000
            if strategy or not self.memory['plans'] or (not steering and day//15 != (self.memory['last_plan_day'] or 0)//15):
                plans = await self.planner.ask('Strategy: set concrete near-term goals and progressively fuzzier week, season, year and three-year direction. Stability is a base for development, not a reason to stop. Match terrain, resources, colony needs and player direction. Short actionable entries.',context,Plans,self.settings.reasoning)
                self.memory['plans'] = plans.model_dump()
                self.memory['last_plan_day'] = day
                self.memory['last_daily_day'] = day
                context['plans'] = self.memory['plans']
                self.persist()
                self.note('plan',plans.response)
                if strategy:
                    self.reply(plans.response)
                    return
            elif not steering and day != self.memory.get('last_daily_day'):
                daily = await self.planner.ask('Daily planning: update today and this week against current work and the existing season/year strategy. Keep entries concrete and short. Do not reset the long-term plan.',context,DailyPlan,self.settings.reasoning)
                self.memory['plans'].update(today=daily.today,week=daily.week,assignments=daily.assignments)
                self.memory['last_daily_day'] = day
                context['plans'] = self.memory['plans']
                self.note('plan',daily.response)
                self.persist()
            roles = self.review_roles(events, steering)
            context['assignments'] = (self.memory['plans'] or {}).get('assignments',{})
            proposals,decision = await self.coordinate(context,roles)
            self.reply(decision.response)
            for role, reason in decision.deferred.items():
                self.note('deferred',reason,role=role)
            if self.mode == 'manual':
                self.note('info','Advice ready. Manual control is on; no game orders sent.')
                return
            for role in decision.accepted:
                for action in Proposal.model_validate(proposals[role]).actions:
                    await self.execute(action,role)
            await self.progress(phase='Watching',detail='Orders issued. Watching their progress.')
        except asyncio.CancelledError:
            self.note('info','Review stopped. New direction and live colony state take priority.')
        except Exception as e:
            self.status = {'phase':'Needs attention','detail':str(e)[:500]}
            self.note('error',str(e)[:1000])
            if steering:
                self.reply('I couldn’t finish that review: '+str(e)[:400])
        finally:
            self.persist()
            self.started_at = None

    def reply(self,text):
        self.memory['chat'].append({'role':'manager','text':text,'at':time.time()})
        self.memory['chat'] = self.memory['chat'][-80:]
        self.persist()
        self.note('reply',text)

    def review_roles(self, events, steering=False):
        assignments = (self.memory['plans'] or {}).get('assignments', {})
        roles = [r for r in assignments if r in ROLES] or list(ROLES)
        key = 'full_review:'+self.colony
        last_full = self.store.get(key)
        if last_full is None:
            self.store.set(key, self.last_review)
        elif self.last_review-last_full >= 60000:
            # Even unassigned domains receive a daily review.
            roles = list(ROLES)
            self.store.set(key, self.last_review)
        if steering:
            # A new player instruction may concern a different domain than the plan.
            roles = list(ROLES)
        types = ' '.join(e['type'] for e in events).lower()
        if any(x in types for x in ('raid','killed','died','mental','letter')):
            roles = list(dict.fromkeys(roles+['Survival','Security','Workforce']))
        return roles

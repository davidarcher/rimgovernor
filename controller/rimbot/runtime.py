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
from .native_models import ConstructionRequest
from .native_client import NativeIntegrationError, StaleObservation
from .http_models import MapResourceOverview, ConstructionWorkOverview
from .resources import resource_brief
from .strategies import StrategyLibrary
from .semantic import semantic_review, reconcile_projects


class Runtime:
    def __init__(self, store, settings=None, *, api_factory=RimAPI, model_factory=LocalModel):
        self.store = store
        self.settings = settings or Settings.model_validate(store.get('settings', {}))
        self.catalog = Catalog()
        self.api_factory, self.model_factory = api_factory, model_factory
        self.api = api_factory(self.settings.rimapi_url, self.catalog)
        self.model = model_factory(self.settings)
        self.manager_model = self.make_manager_model()
        self.planner = Planner(self)
        self.strategies = StrategyLibrary()
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

    def make_manager_model(self):
        name=self.settings.manager_model.strip()
        return self.model_factory(self.settings.model_copy(update={'model':name})) if name and name!=self.settings.model else None

    def model_for_role(self, role):
        return self.manager_model if (role in ROLES or role.startswith('Executor:') or role.startswith('Administrator:')) and self.manager_model is not None else self.model

    @staticmethod
    def empty_memory():
        return {'direction':[], 'plans':None, 'goals':[], 'work':[], 'chat':[], 'last_plan_day':None, 'last_daily_day':None, 'projects':[]}

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
        memory=dict(self.memory)
        if memory.get('spatial_layout'):
            memory['spatial_layout']={k:v for k,v in memory['spatial_layout'].items() if k!='observed_land'}
        return {'mode':self.mode, 'connected':self.connected, 'colony':self.colony,
                'busy':self.busy(), 'status':self.status, 'started_at':self.started_at,
                'counters':self.counters, 'settings':self.settings.model_dump(),
                'observation':compact(self.observation, 100000), 'memory':memory,
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
        if self.manager_model is not None:await self.manager_model.close()

    async def cancel(self):
        self.generation += 1
        if self.busy() and not self.executing:
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)

    async def cancel_project(self, project_id):
        project=next((p for p in self.memory.get('projects',[]) if p['project_id']==project_id),None)
        if project is None:raise ValueError('Project not found')
        memory=self.memory
        await self.cancel()
        # Let a command already sent finish; generation checks prevent the next one.
        if self.busy():await asyncio.gather(self.task,return_exceptions=True)
        if self.memory is not memory:raise ValueError('Colony changed while cancelling the project')
        project.update(status='retired',cancelled_by_player=True,cancelled_direction=list(self.memory.get('direction',[])),feedback=['Cancelled by player'])
        shared={i for p in self.memory['projects'] if p.get('status')!='retired' for i in p['work_ids']}
        for work in self.memory['work']:
            if work['id'] in set(project['work_ids'])-shared and work['status']!='complete':
                work.update(status='dismissed',detail='Project cancelled by player; game orders unchanged')
        self.memory['last_daily_day']=None
        self.last_review=-100000
        self.note('project_cancelled',project['outcome'],project_id=project_id)
        self.persist()

    async def configure(self, settings):
        self.mode = 'manual'
        await self.cancel()
        if self.executing:
            raise ValueError('An action is finishing; retry settings when it completes.')
        async with self.poll_lock:
            await self.api.close()
            await self.model.close()
            if self.manager_model is not None:await self.manager_model.close()
            self.settings = settings
            self.catalog = Catalog()
            self.api = self.api_factory(settings.rimapi_url, self.catalog)
            self.model = self.model_factory(settings)
            self.manager_model = self.make_manager_model()
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
                try:
                    await self.api.warm_discovery()
                except (APIError,ValueError) as error:
                    self.note('error','Definition search index unavailable: '+str(error))
                self.note('connection', 'Colony connected. Ready for your direction.')
                if self.memory.get('spatial_layout',{}).get('version'):
                    self.note('spatial_plan','Loaded the existing master plan',role='Architect',event='base_plan_loaded',version=self.memory['spatial_layout']['version'])
            if self.last_tick is not None and tick < self.last_tick:
                self.mode = 'manual'
                await self.cancel()
                # Loading an earlier save invalidates pending actions, not player goals.
                self.memory['work'] = []
                self.api.invalidate(definitions=True)
                try:
                    await self.api.warm_discovery()
                except (APIError,ValueError) as error:
                    self.note('error','Definition search index unavailable: '+str(error))
                self.note('connection', 'Earlier save loaded. Work tracking refreshed; control is Manual.')
            self.last_tick = tick
            if not observed['game'].get('is_paused'):
                self.initial_pause_session=None
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
                if self.mode == 'automate' and not self.busy() and (due or changed):
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

    async def pause_initial_planning(self):
        if self.mode!='automate' or self.memory.get('plans'):return
        game=await self.api.call('get_game_state',{},fresh=True)
        if game.get('is_paused'):return
        session=game.get('session_id')
        await self.api.request('POST','/api/v1/game/speed',params={'speed':0})
        check=await self.api.call('get_game_state',{},fresh=True)
        if not check.get('is_paused') or check.get('session_id')!=session:
            self.initial_pause_session=None
            raise ValueError('Could not verify initial planning pause')
        self.initial_pause_session=session
        self.note('info','Paused for initial colony planning. Will resume at normal speed for execution.')

    async def resume_initial_planning(self):
        session=getattr(self,'initial_pause_session',None)
        self.initial_pause_session=None
        if not session:return
        game=await self.api.call('get_game_state',{},fresh=True)
        if game.get('session_id')==session and game.get('is_paused'):
            await self.api.request('POST','/api/v1/game/speed',params={'speed':1})
            self.note('info','Initial planning finished. Resumed at normal speed.')

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
            raise ValueError('Enter a direction of 1Ã¢â‚¬â€œ3000 characters.')
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

    async def validate_labor_order(self, action):
        if action.endpoint=='post_colonist_time_assignment':
            assignments=await self.api.call('get_time_assignments',{})
            names={v['name'] for v in assignments}
            if action.arguments['assignment'] not in names:
                raise ValueError('Unknown timetable assignment. Use a native assignment: '+', '.join(sorted(names))+'. Timetables do not unforbid supplies or issue specific jobs. No order sent.')
            if not 0<=action.arguments['hour']<=23:raise ValueError('Timetable hour must be 0 through 23. No order sent.')
            return
        if action.endpoint not in ('post_colonist_work_priority','post_colonists_work_priority'):return
        definitions=await self.api.call('get_def_all',{'filters':['WorkTypeDefs']})
        names={d['def_name'] for d in (definitions.get('work_type_defs') or [])}
        if not names:raise ValueError('Native work types unavailable; no priority order sent.')
        for item in action.arguments.get('priorities',[action.arguments]):
            if item.get('work') not in names:
                raise ValueError(f"Unknown work type {item.get('work')!r}. Use a native work category: {', '.join(sorted(names))}. Building names and JobDefs are not work types. No priority order sent.")

    async def action_complete(self, action, receipt=None):
        if action.endpoint=='zone_growing_cells':
            if not receipt:return False
            zone=await self.api.call('get_map_zone_growing',{'map_id':action.arguments['map_id'],'zone_id':receipt['zone_id']},fresh=True)
            expected={(c['x'],c['z']) for c in action.arguments['cells']}
            if not expected or zone.get('plant_def_name')!=action.arguments['plant_def'] or zone.get('zone',{}).get('cells_count')!=len(expected):return False
            if {(c['x'],c['z']) for c in receipt['cells']}!=expected:return False
            x0=min(x for x,z in expected);x1=max(x for x,z in expected);z0=min(z for x,z in expected);z1=max(z for x,z in expected)
            center={'x':(x0+x1)//2,'z':(z0+z1)//2};radius=max(x1-center['x'],z1-center['z'])
            if radius>32:return False
            area=await self.api.call('construction_area',{'map_id':action.arguments['map_id'],'center':center,'radius':radius})
            actual={(c.position.x,c.position.z) for c in area.cells if c.zone_id==receipt['zone_id']}
            return actual==expected
        if action.endpoint=='construction_place':
            state=await self.api.native.inspect(ConstructionRequest.model_validate(action.arguments))
            return state.accepted and all(item.state=='built' for item in state.items)
        if action.endpoint in ('post_map_zone_growing','post_map_zone_stockpile'):
            if not receipt:return False
            args=action.arguments
            growing=action.endpoint=='post_map_zone_growing'
            zone_id=(receipt.get('zone') or {}).get('id') if growing else receipt.get('zone_id')
            if zone_id is None or (not growing and receipt.get('success') is not True):return False
            a,b=args['point_a'],args['point_b']
            expected=(abs(a['x']-b['x'])+1)*(abs(a['z']-b['z'])+1)
            zones=await self.api.call('get_map_zones',{'map_id':args['map_id']},fresh=True)
            zone=next((z for z in zones.get('zones',[]) if z['id']==zone_id),None)
            if not zone or zone['cells_count']!=expected:return False
            if growing:
                actual=await self.api.call('get_map_zone_growing',{'map_id':args['map_id'],'zone_id':zone_id},fresh=True)
                return actual.get('plant_def_name')==args['plant_def'] and (actual.get('zone') or {}).get('id')==zone_id
            if args.get('name') is not None and zone.get('label')!=args['name']:return False
            if args.get('priority') is not None and receipt.get('priority')!=args['priority']:return False
            return True
        if action.endpoint=='delete_map_zone_stockpile_delete':
            zones=await self.api.call('get_map_zones',{'map_id':self.observation['map']['id']},fresh=True)
            return all(z['id']!=action.arguments['zone_id'] for z in zones.get('zones',[]))
        if action.endpoint=='post_pawn_edit_status' and set(action.arguments)=={'pawn_id','hostility_response'}:
            args=action.arguments
            pawn=await self.api.call('get_colonist_detailed',{'id':args['pawn_id']},fresh=True)
            # Native RimWorld.HostilityResponseMode, verified against the game assembly.
            responses={'Ignore':0,'Attack':1,'Flee':2}
            return args['hostility_response'] in responses and pawn.get('policies_info',{}).get('hostility_response')==responses[args['hostility_response']]
        if action.endpoint=='post_jobs_make_equip':
            inventory=await self.api.call('get_pawns_inventory',{'id':action.arguments['pawn_id']},fresh=True)
            return any(item['thing_id']==action.arguments['item_id'] for item in inventory.get('equipment',[])+inventory.get('apparels',[]))
        if action.done is not None:return await self.check(action.done)
        args=action.arguments
        if action.endpoint=='post_work_settings':
            return (await self.api.call('get_work_settings',{},fresh=True))['use_work_priorities']==args['use_work_priorities']
        if action.endpoint=='orders_unforbid_all':
            state=await self.api.call('orders_forbidden_overview',args,fresh=True)
            return state.remaining_eligible_count==0
        if action.endpoint=='post_things_set_forbidden':
            items=await self.api.call('get_map_things',{'map_id':args['map_id']},fresh=True)
            found={item['thing_id']:item for item in items}
            return all(i in found and found[i]['is_forbidden']==args['forbidden'] for i in args['thing_ids'])
        if action.endpoint in ('post_colonist_work_priority','post_colonists_work_priority'):
            for priority in args.get('priorities',[args]):
                pawn=await self.api.call('get_colonist_detailed',{'id':priority['id']},fresh=True)
                priorities=pawn.get('colonist_work_info',{}).get('work_priorities',[])
                actual=next((p['priority'] for p in priorities if p['work_type']==priority['work']),0)
                if actual!=priority['priority']:return False
            return True
        return False

    async def reconcile(self):
        changed = False
        for work in self.memory['work']:
            action=work.get('action',{})
            if (work['status'] in ('issued','unknown','waiting','unresolved')
                and action.get('endpoint')=='post_order_designate_area'
                and action.get('arguments',{}).get('type','').lower() not in ('mine','deconstruct','harvest','hunt','remove-all')):
                work['status']='rejected'
                work['detail']='Unsupported designation type; this order made no game changes'
                changed=True
                continue
            if work['status'] not in ('issued','unknown','waiting'):
                continue
            try:
                done = await self.action_complete(Action.model_validate(work['action']),work.get('native_result'))
                if done:
                    work['status'] = 'complete'
                    work['detail'] = 'Verified in the colony'
                    title=work.get('title') or work['action'].get('title','Order')
                    self.note('work_outcome',title,role=work.get('role','Colony'),work_id=work['id'],project_id=work.get('project_id'),results=[{'id':work['id'],'title':title,'status':'complete'}])
                    changed = True
                # Unknown/pending observations never block replacement actions.
                # Expire tracking after a day; the next review inspects live state.
                elif self.last_tick - work.get('tick',self.last_tick) > 60000:
                    work['status'] = 'unresolved'
                    work['detail'] = 'Not observed after a day; needs a fresh assessment'
                    changed = True
            except (ValueError, APIError, NativeIntegrationError):
                work['detail'] = 'Waiting for a fresh observation'
        changed=reconcile_projects(self.memory) or changed
        if changed:
            self.persist()
            self.note('work','Work updated from colony observations.')

    async def execute(self, action: Action, role):
        self.check_generation()
        if self.mode != 'automate':
            return
        e = self.catalog.validate(action.endpoint, action.arguments, True)
        await self.validate_build_materials(action)
        await self.validate_labor_order(action)
        if 'map_id' in action.arguments and action.arguments['map_id'] != self.observation['map']['id']:
            raise ValueError('Action targets a different map.')
        if await self.action_complete(action):
            self.note('action',f'Already done: {action.title}',role=role)
            return
        for requirement in action.requires:
            if not await self.check(requirement):
                raise ValueError(f'Prerequisite changed: {action.title}')
        # Only in-flight duplicates are suppressed; saved work never owns a site.
        for existing in self.memory['work']:
            previous=existing.get('action',{})
            if existing['status'] in ('issued','unknown','waiting') and previous.get('endpoint')==action.endpoint and previous.get('arguments')==action.arguments:
                self.note('action',f'Already in flight: {action.title}',role=role)
                return
        work = {'id':uuid.uuid4().hex[:12], 'title':action.title,'status':'unknown',
                'detail':'Sending order', 'role':role,'action':action.model_dump(), 'tick':self.last_tick}
        self.memory['work'].append(work)
        self.memory['work'] = self.memory['work'][-160:]
        self.persist()  # Unknown before sending: restart cannot imply success.
        self.executing = True
        try:
            self.check_generation()
            # Player commands are valid while paused; never change the game speed here.
            game = await self.api.call('get_game_state',{},fresh=True)
            if game.get('session_id') != self.observation.get('game',{}).get('session_id'):
                work['status'] = 'cancelled'
                work['detail'] = 'Colony changed before this order was sent'
                self.mode = 'manual'
                return
            if action.endpoint=='construction_place':
                result=await self.api.native.place(ConstructionRequest.model_validate(action.arguments),action.observation_basis)
            else:
                result = await self.api.call(action.endpoint, action.arguments, write=True)
                if e.get('native_contract'):result=result.model_dump()
            if action.endpoint=='construction_place':
                if not result.accepted:
                    work['status']='rejected'
                    work['detail']='; '.join(item.reason for item in result.items if item.reason)
                    self.note('error',work['detail'],role=role)
                    return
                work['native_result']=result.model_dump()
                result=result.model_dump()
            if action.endpoint=='zone_growing_cells':
                work['native_result']=result
                self.persist()
            if action.endpoint in ('post_map_zone_growing','post_map_zone_stockpile'):
                work['native_result']=result
                self.persist()
                if action.endpoint=='post_map_zone_stockpile' and result.get('success') is not True:
                    work['status']='rejected'
                    work['detail']=result.get('message') or 'The game rejected the stockpile'
                    self.note('error',work['detail'],role=role)
                    return
            work['status'] = 'issued'
            work['detail'] = 'Order issued; checking the colony'
            self.counters['actions'] += 1
            self.note('action',action.title,role=role,endpoint=e['path'],arguments=action.arguments,result=compact(result,5000))
            if await self.action_complete(action,work.get('native_result')):
                work['status'], work['detail'] = 'complete', 'Verified in the colony'
        except StaleObservation as error:
            work['status']='deferred'
            work['detail']='Colony construction changed; this draft needs a fresh review'
            self.note('deferred',work['detail'],role=role)
            self.last_review=-100000
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
        if any('Workforce' in p['labor'] for p in proposals.values()):
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

    async def manager_context(self, context):
        """Refresh observations between managers; never carry forward a stale bed count."""
        observed=await snapshot(self.api,self.settings.map_id)
        if self.observation.get('game',{}).get('session_id') != observed['game'].get('session_id'):
            raise ModelError('Colony changed during review; pending decisions were discarded.')
        self.observation=observed
        result={**context,'colony':compact(observed,20000)}
        if 'get_work_settings' in self.catalog.available:
            result['work_settings']=await self.api.call('get_work_settings',{},fresh=True)
        if 'construction_state' in self.catalog.available:
            state=await self.api.call('construction_state',{'map_id':observed['map']['id']})
            result['construction_state']=state.model_dump()
            # Store an observed starting location once per colony. It is context,
            # not a placement rule: player direction may choose another site.
            if 'colony_focus' not in self.memory:
                points=[b.position.model_dump() for b in state.buildings if b.state=='built']
                if not points:
                    points=[p.get('colonist',{}).get('position') for p in observed.get('pawns',[])]
                    points=[p for p in points if p and 'x' in p and 'z' in p]
                if points:
                    import statistics
                    self.memory['colony_focus']={k:int(statistics.median(p[k] for p in points)) for k in ('x','z')}
                    self.persist()
            result['colony_focus']=self.memory.get('colony_focus')
        return await self.observe_work(result)

    async def observe_work(self, context):
        if 'get_map_construction_work' not in self.catalog.available:
            return {**context,'construction_work':{'available':False,'reason':'Native work observations are unavailable.'}}
        try:
            args={'map_id':self.observation['map']['id'],'offset':0,'limit':16}
            work=ConstructionWorkOverview.model_validate(await self.api.call('get_map_construction_work',args,fresh=True))
            self.store.event(self.colony,'tool_result',role='Observation',tool='get_map_construction_work',arguments=args,result=work.model_dump())
            return {**context,'construction_work':work.model_dump()}
        except (APIError,ValueError) as error:
            self.note('error','Work observations unavailable: '+str(error))
            return {**context,'construction_work':{'available':False,'reason':str(error)}}

    async def observe_resources(self, context):
        if 'get_map_resource_overview' not in self.catalog.available:
            return {**context,'resource_overview':{'available':False,'reason':'Installed RIMAPI does not expose the resource overview.'}}
        try:
            if self.memory.get('colony_focus') is None:
                context=await self.manager_context(context)
            center=self.memory.get('colony_focus')
            if center is None:
                raise ValueError('No observed colony location; resource survey needs a center.')
            args={'map_id':self.observation['map']['id'],'center_x':center['x'],'center_z':center['z'],'nearby_radius':40}
            overview=MapResourceOverview.model_validate(await self.api.call('get_map_resource_overview',args,fresh=True))
            brief=resource_brief(overview)
            self.store.event(self.colony,'tool_result',role='Observation',tool='get_map_resource_overview',arguments=args,result=brief)
            return {**context,'resource_overview':brief}
        except (APIError,ValueError) as error:
            self.note('error','Resource overview unavailable: '+str(error))
            return {**context,'resource_overview':{'available':False,'reason':str(error)}}

    async def review(self, steering=False, strategy=False):
        self.cycle_generation = self.generation
        self.started_at = time.time()
        self.counters = {k:0 for k in self.counters}
        events = self.events_pending
        self.events_pending = []
        self.last_review_wall = time.monotonic()
        self.last_review = self.last_tick or 0
        try:
            await self.pause_initial_planning()
            context = {'colony':compact(self.observation,20000),'player_direction':self.memory['direction'],
                       'goals':self.memory['goals'],'plans':self.memory['plans'],
                       'work':[{k:v for k,v in w.items() if k!='action'} for w in self.memory['work'][-30:]],
                       'notifications':compact(events,6000), 'mode':self.mode,
                       'capabilities':self.catalog.listing()}
            context=await self.observe_resources(context)
            if 'construction_work' not in context:context=await self.observe_work(context)
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
            context['administration_required']=steering or any(any(word in e.get('type','').lower() for word in ('raid','killed','died')) for e in events)
            context['assignments'] = (self.memory['plans'] or {}).get('assignments',{})
            if self.memory.get('spatial_layout',{}).get('version'):
                from .base_plan import request_review
                types=' '.join(e.get('type','') for e in events).lower()
                if 'research' in types:request_review(self,'technology','Research event: review whether the layout needs a change')
                if any(word in types for word in ('colonistkilled','colonistdied','defeat')):request_review(self,'defensive_failure','Colony loss event: review defensive layout')
            await semantic_review(self,context,roles)
            await self.progress(phase='Watching',detail='Review finished. Watching approved work.')
        except asyncio.CancelledError:
            self.note('info','Review stopped. New direction and live colony state take priority.')
        except Exception as e:
            self.status = {'phase':'Needs attention','detail':str(e)[:500]}
            self.note('error',str(e)[:1000])
            if steering:
                self.reply('I couldnÃ¢â‚¬â„¢t finish that review: '+str(e)[:400])
        finally:
            await self.resume_initial_planning()
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
        return [r for r in roles if r!='Workforce'] or [r for r in ROLES if r!='Workforce']

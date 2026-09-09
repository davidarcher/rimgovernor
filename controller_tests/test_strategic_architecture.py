import json
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import ObservationBatch, project
from rimbot.colony_plan import ColonyPlan, PlanSpec, Decision, RoomShell
from rimbot.config import Settings, ModelRole, ModelRouting, load_model_routing
from rimbot.consultation import Consultations
from rimbot.hands import room_placements, validate_geometry
from rimbot.model_router import ModelRouter
from rimbot.strategic_state import StrategicState, features, context
from rimbot.store import Store


def batch():
    status = {'status':'game_loaded','time':{'mapName':'Test','ticksGame':100,'paused':True},
        'alerts':[],'counts':{'hostileCount':0,'huntingPredatorCount':0}}
    native = {'status_before':status,'status_after':deepcopy(status),
        'pawns':{'pawns':[{'thingId':'Pawn1','name':'A','position':{'x':20,'z':20},'job':'Construct',
            'drafted':False,'downed':False,'dead':False,'needs':{'mood':.5,'food':.8,'rest':.8},
            'equipment':None,'health':None}]}, 'supplies':{'things':[]},
        'buildings':{'attention':{},'resourceDeficit':[],'powerNets':[]},
        'rooms':{'rooms':[]}, 'zones':{'zoneCount':0}}
    return ObservationBatch(project(native), native)


def decision(plan=None, revision=0):
    return Decision(expected_revision=revision, disposition='revise' if plan else 'continue',
        assessment='Shelter first', rationale='Current evidence supports the plan', reply='Continue shelter work.', plan=plan)


def room_plan():
    return PlanSpec(long_term='A stable colony', goals=['Shelter'], steps=[{
        'id':'shelter', 'title':'Shelter shell', 'completion_criteria':'Walls and door built; roof remains separate',
        'action':{'kind':'build_room_shell','bounds':{'x':20,'z':20,'width':7,'height':7},
            'wall_def':'Wall','door_def':'Door','materials':['WoodLog'],'entrance':'south'}}])


@pytest.mark.asyncio
async def test_invalid_native_step_rejected_before_plan_commit(tmp_path):
    rt = runtime(tmp_path)
    await rt.sync_identity()
    rt.batch = batch()
    rt.game.describe = AsyncMock(return_value={'type':'object','properties':{'pawn':{'type':'string'}},
                                               'additionalProperties':False})
    rt.game.invoke = AsyncMock()
    spec = PlanSpec(steps=[dict(id='equip',title='Equip',completion_criteria='Equipped',
        action=dict(kind='native_operation',tool='home/pawn_config',arguments={'pawn_ids':['guessed']}))])
    before = rt.current_plan.model_dump()
    with pytest.raises(ValueError, match='Plan step equip: home/pawn_config') as error:
        await rt.commit_strategy(decision(spec), actor=ModelRole.STRATEGIST,
            expected_token=rt.context_token, expected_revision=rt.chat_revision)
    assert 'pawn_ids' in str(error.value) and len(str(error.value)) < 350
    assert rt.current_plan.model_dump() == before
    rt.game.invoke.assert_not_awaited()
    rt.store.close()


def runtime(tmp_path, **kwargs):
    store = Store(tmp_path/'state.sqlite')
    rt = BridgeRuntime(store, tmp_path, **kwargs)
    rt.game = SimpleNamespace(query=AsyncMock(return_value={'colonyId':'test','mapId':1,'loadToken':'load'}),
        invoke=AsyncMock(return_value={'success':True,'canPlace':True,'costList':[{'defName':'WoodLog','count':5}], 'materials':{'rows':[{'defName':'WoodLog','available':1000}]}}))
    return rt


def test_only_strategist_can_commit_and_identical_plan_is_sticky():
    plan=ColonyPlan(); proposed=decision(room_plan())
    for role in (ModelRole.ANALYST,ModelRole.ARCHITECT,ModelRole.CRITIC):
        with pytest.raises(PermissionError):plan.commit(proposed,actor=role,tick=100)
    assert plan.revision==0
    assert plan.commit(proposed,actor=ModelRole.STRATEGIST,tick=100)
    assert not plan.commit(decision(room_plan(),1),actor=ModelRole.STRATEGIST,tick=200)
    assert not plan.commit(decision(revision=1),actor=ModelRole.STRATEGIST,tick=300)
    assert plan.revision==1 and plan.chosen_tick==100


def test_complete_room_perimeter_door_and_spatial_reservations():
    spec=room_plan(); room=spec.steps[0].action; placements=room_placements(room)
    assert len(placements)==24 and sum(p.def_name=='Door' for p in placements)==1
    assert (placements[0].x,placements[0].z)==(23,20)
    assert len({(p.x,p.z) for p in placements})==24
    validate_geometry(spec)
    data=spec.model_dump();data['reserved_walkways']=[{'x':20,'z':20,'width':1,'height':7}]
    with pytest.raises(ValueError,match='walkway'):validate_geometry(PlanSpec.model_validate(data))


def test_noop_world_updates_do_not_wake_brain_but_downed_pawn_does():
    state=StrategicState(); current=batch()
    assert state.update(current)
    state.decided()
    for tick in (101,102,103):
        current.summary.end_tick=tick
        assert not state.update(current)
    current.summary.pawns[0].downed=True
    assert state.update(current)
    assert any(e['kind']=='pawn.downed' for e in state.pending)


def test_mood_hysteresis_does_not_chatter_and_missing_nutrition_is_unknown():
    state=StrategicState();current=batch();state.update(current);state.decided()
    current.summary.pawns[0].mood=.24
    assert state.update(current);state.decided()
    for mood in (.26,.24,.30,.34):
        current.summary.pawns[0].mood=mood
        assert not state.update(current)
    current.summary.pawns[0].mood=.36
    assert state.update(current)
    assert features(current)['resources']['food_days'] is None


@pytest.mark.asyncio
async def test_generic_adviser_cannot_dispatch_actions_or_recurse(tmp_path):
    calls=[]
    class BadModel:
        async def complete(self,messages,tools,*args):
            calls.append(tools)
            return {'tool_calls':[{'function':{'name':'native','arguments':'{}'}}]},{}
        async def close(self):pass
    store=Store(tmp_path/'advice.sqlite')
    router=ModelRouter(ModelRouting(roles={ModelRole.ANALYST:Settings()}),store,lambda _:BadModel())
    service=Consultations(router)
    with pytest.raises(ValueError,match='structured report'):
        await service.ask('analyst','Explain this shortage',{'load_token':'a','tick':1},AsyncMock())
    assert [t['function']['name'] for t in calls[0]]==['report']
    with pytest.raises(ValueError,match='recursively'):
        await service.ask('strategist','Ask yourself',{},AsyncMock())
    await router.close();store.close()


@pytest.mark.asyncio
async def test_player_room_command_and_plan_survive_model_restart(tmp_path):
    calls=[]
    class Brain:
        async def complete(self,messages,tools,*args):
            calls.append(tools)
            if len(calls)>1: return {'role':'assistant','content':'Room shell queued.'}, {}
            return {'role':'assistant','tool_calls':[{'id':'d','type':'function','function':{
                'name':'BuildRoom','arguments':json.dumps({'intent_id':'bedroom','room':room_plan().steps[0].action.model_dump()})}}]}, {'prompt_tokens':100,'completion_tokens':50}
        async def close(self):pass
    rt=runtime(tmp_path,model_factory=lambda _:Brain())
    await rt.sync_identity();rt.batch=batch();rt.strategic_state.update(rt.batch)
    await rt.planner.play_bridge()
    assert rt.current_plan.revision==1 and rt.current_plan.spec.steps[0].source=='PLAYER'
    assert 'consult' not in [t['function']['name'] for t in calls[0]]
    assert all(t['function']['name'] not in ('native','control_clock') for t in calls[0])
    await rt.router.close();rt.store.close()
    other=runtime(tmp_path,settings=Settings(model='another-local-model'))
    await other.sync_identity()
    assert other.current_plan.spec.steps[0].action==room_plan().steps[0].action and other.current_plan.revision==1
    assert other.mode=='manual'
    other.store.close()


@pytest.mark.asyncio
async def test_chat_question_can_finish_with_prose_without_any_orders(tmp_path):
    class Brain:
        async def complete(self,messages,tools,*args):
            return {'role':'assistant','content':'Shelter is not built yet.'}, {}
        async def close(self):pass
    rt=runtime(tmp_path,model_factory=lambda _:Brain())
    await rt.sync_identity();rt.batch=batch();rt.strategic_state.update(rt.batch)
    await rt.planner.play_bridge()
    assert rt.current_plan.revision==0 and rt.counters['actions']==0
    assert rt.chat[-1]['text']=='Shelter is not built yet.'
    await rt.router.close();rt.store.close()


@pytest.mark.asyncio
async def test_discovered_native_tool_is_callable_in_the_same_review(tmp_path):
    count=0
    class Brain:
        async def complete(self,messages,tools,*args):
            nonlocal count
            count+=1
            if count==1:
                name,payload='describe',{'name':'home/list_things'}
            elif count==2:
                assert json.loads(messages[-1]['content'])['callable_tool']=='native_home__list_things'
                assert any(t['function']['name']=='native_home__list_things' for t in tools)
                name,payload='native_home__list_things',{'category':'food'}
            elif count==3:
                assert json.loads(messages[-1]['content'])['things']==[]
                identity=json.loads(messages[-1]['content'])['review_evidence_id']
                name,payload='review_evidence',{'operation':'read','id':identity}
            else:
                assert json.loads(messages[-1]['content'])['result']['things']==[]
                return {'role':'assistant','content':'No food was observed.'},{}
            return {'role':'assistant','tool_calls':[{'id':str(count),'type':'function',
                'function':{'name':name,'arguments':json.dumps(payload)}}]},{}
        async def close(self):pass
    rt=runtime(tmp_path,model_factory=lambda _:Brain())
    await rt.sync_identity();rt.batch=batch();rt.strategic_state.update(rt.batch)
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{
        'category':{'type':'string','enum':['food']}},'required':['category'],'additionalProperties':False})
    rt.game.invoke=AsyncMock(return_value={'things':[]})
    await rt.planner.play_bridge()
    rt.game.invoke.assert_awaited_once_with('home/list_things',{'category':'food'},allow_write=False)
    assert count==4 and rt.counters['actions']==0
    await rt.router.close();rt.store.close()


@pytest.mark.asyncio
async def test_invented_construction_is_repaired_before_any_plan_is_saved(tmp_path):
    turns=0
    class Brain:
        async def complete(self,messages,tools,*args):
            nonlocal turns
            turns+=1
            spec=room_plan()
            if turns>2: return {'role':'assistant','content':'Validated room shell queued.'},{}
            if turns==1:spec.steps[0].action.door_def='Door_Wood'
            else:
                error=json.loads(messages[-1]['content'])
                assert error['construction']['definition']=='Door_Wood'
                assert rt.current_plan.revision==0 and not rt.current_plan.history
            return {'role':'assistant','tool_calls':[{'id':str(turns),'type':'function',
                'function':{'name':'BuildRoom','arguments':json.dumps({'intent_id':'bedroom','room':spec.steps[0].action.model_dump()})}}]},{}
        async def close(self):pass
    async def preview(name,args,**kwargs):
        assert args['dryRun'] is True and kwargs['allow_write'] is False
        if args['defName']=='Door_Wood':raise ValueError('Unknown definition')
        return {'canPlace':True,'success':True,'costList':[{'defName':'WoodLog','count':5}], 'materials':{'rows':[{'defName':'WoodLog','available':1000}]}}
    rt=runtime(tmp_path,model_factory=lambda _:Brain())
    await rt.sync_identity();rt.batch=batch();rt.strategic_state.update(rt.batch)
    rt.game.invoke=AsyncMock(side_effect=preview)
    await rt.planner.play_bridge()
    assert turns==2 and rt.current_plan.revision==1
    assert rt.current_plan.spec.steps[0].action==room_plan().steps[0].action and rt.counters['actions']==0
    await rt.router.close();rt.store.close()


@pytest.mark.asyncio
async def test_inspection_refuses_writes_even_in_automate(tmp_path):
    rt=runtime(tmp_path);rt.mode='automate';rt.game.invoke=AsyncMock()
    with pytest.raises(PermissionError):
        await rt.inspect_native('home/order',{'action':'draft','pawn':'Pawn1','dryRun':False})
    rt.game.invoke.assert_not_awaited();rt.store.close()


@pytest.mark.asyncio
async def test_blocked_hands_returns_structured_failure_without_model_call(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.mode='automate'
    rt.current_plan.commit(decision(room_plan()),actor=ModelRole.STRATEGIST,tick=100)
    rt.game.query=AsyncMock(return_value={'buildings':[]})
    rt.inspect_native=AsyncMock(return_value={'canPlace':False,'materials':{'canBuildNow':False,'missing':'wood'}})
    rt.native=AsyncMock()
    await rt.hands.advance(rt)
    progress=rt.current_plan.progress['shelter']
    assert progress.state=='blocked' and progress.failure.code=='construction_unavailable'
    assert progress.failure.retryable and not rt.router.clients
    rt.native.assert_not_awaited();rt.store.close()


def test_player_cancellation_cannot_be_renamed_into_duplicate_work():
    plan=ColonyPlan();plan.commit(decision(room_plan()),actor=ModelRole.STRATEGIST,tick=100)
    plan.cancel('shelter')
    replacement=room_plan();replacement.steps[0].id='new_shelter'
    with pytest.raises(ValueError,match='cancelled'):
        plan.commit(decision(replacement,2),actor=ModelRole.STRATEGIST,tick=101)


def test_plan_dependencies_reject_cycles():
    data=room_plan().model_dump();data['steps'][0]['after']=[{'step':'shelter'}]
    with pytest.raises(ValueError,match='cycle'):PlanSpec.model_validate(data)


def test_model_config_is_local_and_auxiliaries_optional():
    routing=load_model_routing(Settings(model='custom'))
    assert list(routing.roles)==[ModelRole.STRATEGIST]
    with pytest.raises(ValueError):Settings(model_url='https://remote.example/v1')


@pytest.mark.asyncio
async def test_new_material_event_invalidates_inflight_decision(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.mode='automate';rt.batch=batch()
    revision=rt.chat_revision
    rt.signal('plan.step_failed', {'step':'shelter'})
    assert rt.chat_revision > revision and rt.wake.is_set()
    with pytest.raises(ValueError,match='changed'):
        await rt.commit_strategy(decision(room_plan()),actor=ModelRole.STRATEGIST,
            expected_token=rt.context_token,expected_revision=revision)
    assert rt.current_plan.revision==0 and rt.strategic_state.pending
    rt.store.close()

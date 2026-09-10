from unittest.mock import AsyncMock
from copy import deepcopy
import pytest
from rimbot.player_commands import apply_command,command_schema,resolve_goal_id,resolve_colonist
from rimbot.colony_plan import ColonyPlan, CommitSteps, PlanStep, StepProgress
from rimbot.resource_accounting import validate_allocations
from rimbot.resource_accounting import validate_execution_costs
from test_strategic_architecture import runtime, batch, room_plan
from test_construction_preflight import footprint, native_reply


@pytest.mark.asyncio
async def test_resource_policy_commands_preserve_other_field_and_other_resources(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    query=rt.game.query
    async def observed(name,**args):
        if name=='home/colony_facts': return {'resources':{'ComponentIndustrial':30,'Steel':100}}
        return await query(name,**args)
    rt.game.query=observed
    rt.current_plan.control['resource_policy']={'Steel':{'reserve':100,'spending':'stop'}}
    async def command(**payload):
        return await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
    await command(kind='ModifyResourcePolicy',resource='ComponentIndustrial',spending='defense_only')
    await command(kind='SetResourceReserve',resource='ComponentIndustrial',reserve=40)
    assert rt.current_plan.control['resource_policy']['ComponentIndustrial']=={'reserve':40,'spending':'defense_only'}
    await command(kind='ModifyResourcePolicy',resource='ComponentIndustrial',spending='normal')
    assert rt.current_plan.control['resource_policy']['ComponentIndustrial']=={'reserve':40,'spending':'normal'}
    await command(kind='SetResourceReserve',resource='ComponentIndustrial',reserve=0)
    assert rt.current_plan.control['resource_policy']=={'Steel':{'reserve':100,'spending':'stop'},
        'ComponentIndustrial':{'reserve':0,'spending':'normal'}}
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError):
        await command(kind='ModifyResourcePolicy',resource='ComponentIndustrial',spending='stop',reserve=30)
    with pytest.raises(ValueError):
        await command(kind='SetResourceReserve',resource='Unknown',reserve=5)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


def test_semantic_schema_has_provider_object_envelope_and_goal_names_resolve_only_unambiguously():
    from rimbot.consultation import structured_tool
    schema=structured_tool('command','Command',command_schema())['function']['parameters']
    assert schema['type']=='object' and schema['required']==['request']
    assert 'request' in schema['properties']
    assert resolve_goal_id('food expansion',{'intent-food-expansion':{}})=='intent-food-expansion'
    with pytest.raises(ValueError,match='ambiguous'):
        resolve_goal_id('food expansion',{'intent-food-expansion':{},'other':{'label':'food expansion'}})


def test_colonist_names_resolve_exactly_and_never_guess_between_duplicates():
    pawns=[{'thingId':'Pawn1','name':'Wobbler'},{'thingId':'Pawn2','name':'Yuto'}]
    assert resolve_colonist(' wObBlEr ',pawns)['thingId']=='Pawn1'
    assert resolve_colonist('Pawn2',pawns)['name']=='Yuto'
    with pytest.raises(ValueError,match='unknown or ambiguous'):resolve_colonist('Wobb',pawns)
    pawns.append({'thingId':'Pawn3','name':'Wobbler'})
    with pytest.raises(ValueError,match='unknown or ambiguous'):resolve_colonist('Wobbler',pawns)
    assert resolve_colonist('Pawn1',pawns)['thingId']=='Pawn1'


@pytest.mark.asyncio
async def test_named_work_command_uses_stable_identity_for_execution_and_override(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    async def query(name,**args):
        if name=='home/list_pawns':
            return {'pawns':[{'thingId':'Pawn1','name':'Wobbler','work':{
                'types':[{'name':'Hauling','disabled':False,'priority':1}]}}]}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query=AsyncMock(side_effect=query)
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{
        'pawn':{'type':'string'},'work':{'type':'string'},'watch':{'type':'boolean'},'dryRun':{'type':'boolean'}},
        'additionalProperties':False})
    result=await apply_command(rt,{'kind':'SetWorkPriority','pawn':'Wobbler','work_type':'Hauling','priority':0},
        token=rt.context_token,revision=rt.chat_revision)
    step=next(s for s in rt.current_plan.spec.steps if s.id==result['step'])
    assert step.source=='PLAYER' and step.action.arguments['pawn']=='Pawn1'
    assert rt.current_plan.control['work_overrides']=={'Pawn1':{'Hauling':0}}
    assert rt.mode=='manual' and rt.counters['actions']==0
    rt.store.close()


@pytest.mark.asyncio
async def test_manual_chat_dispatches_only_current_player_order_without_resuming_clock(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{'set':{'type':'string'},
        'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}},'additionalProperties':False})
    rt.game.invoke=AsyncMock(return_value={'write':{'resolved':{'defName':'GeothermalPower'},'refused':False}})
    result=await apply_command(rt,{'kind':'SetResearch','project':'GeothermalPower'},token=rt.context_token,revision=rt.chat_revision)
    auto=rt.current_plan.spec.steps[0].model_copy(deep=True)
    auto.id='autopilot';auto.source='AUTOPILOT';auto.action.arguments['set']='Battery'
    rt.current_plan.spec.steps.append(auto);rt.current_plan.progress[auto.id]=StepProgress()
    rt.native=AsyncMock(return_value={'receipt':{'outcome':'configured'}})
    rt.handled_revision=rt.chat_revision
    await rt.execute_manual_requests()
    assert rt.mode=='manual' and rt.manual_execution is None
    assert rt.current_plan.progress[result['step']].state=='complete'
    assert rt.current_plan.progress['autopilot'].state=='pending'
    assert rt.native.await_count==1
    assert rt.native.await_args.args[0]=='home/research'
    rt.store.close()


@pytest.mark.asyncio
async def test_manual_order_is_invalidated_by_new_player_direction(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.mode='manual'
    rt.manual_requests=[('old',rt.context_token,rt.chat_revision)]
    rt.chat_revision+=1
    rt.hands.advance=AsyncMock()
    await rt.execute_manual_requests()
    rt.hands.advance.assert_not_awaited()
    rt.store.close()


def test_policy_change_blocks_previously_reserved_construction_at_dispatch():
    plan=ColonyPlan()
    plan.spec.steps=[PlanStep(id='cooler',title='Cooler',source='PLAYER',purpose='storage',
        action={'kind':'place_buildings','placements':[{'def_name':'Cooler','x':10,'z':10}]},completion_criteria='Built')]
    plan.progress['cooler']=StepProgress()
    plan.control={'costs':{'cooler':{'0':{'ComponentIndustrial':3}}}}
    preview={'costList':[{'defName':'ComponentIndustrial','count':3}],
        'materials':{'rows':[{'defName':'ComponentIndustrial','available':10}]}}
    validate_execution_costs(plan,plan.progress['cooler'],'0',preview)
    plan.control['resource_policy']={'ComponentIndustrial':{'spending':'defense_only','reserve':0}}
    with pytest.raises(ValueError,match='policy'): validate_execution_costs(plan,plan.progress['cooler'],'0',preview)


@pytest.mark.asyncio
async def test_player_research_request_is_validated_and_queued_for_same_hands(tmp_path):
    rt=runtime(tmp_path); await rt.sync_identity();rt.batch=batch()
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{'set':{'type':'string'},
        'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}},'additionalProperties':False})
    rt.game.invoke=AsyncMock(return_value={'write':{'resolved':{'defName':'GeothermalPower'},'refused':False}})
    result=await apply_command(rt,{'kind':'SetResearch','project':'geothermal'},token=rt.context_token,revision=rt.chat_revision)
    step=rt.current_plan.spec.steps[0]
    assert step.source=='PLAYER' and step.action.tool=='home/research'
    assert step.action.arguments=={'set':'GeothermalPower','dryRun':False,'watch':False}
    assert rt.current_plan.progress[result['step']].state=='pending'
    assert list(rt.current_plan.ready())==[step]
    assert rt.counters['actions']==0
    rt.game.invoke.assert_awaited_once_with('home/research',{'set':'geothermal','dryRun':True,'watch':False},allow_write=False)
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('case',['locked','ambiguous','missing','direction'])
async def test_research_preflight_failure_never_commits_a_player_order(tmp_path,case):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    before=rt.current_plan.model_dump();revision=rt.chat_revision
    async def preview(*args):
        if case=='direction':
            rt.chat_revision+=1
            return {'write':{'refused':False,'resolved':{'defName':'GeothermalPower'}}}
        if case=='missing': return {}
        return {'write':{'refused':True,'reason':'Unfinished prerequisite' if case=='locked' else 'Ambiguous name'}}
    rt.inspect_native=AsyncMock(side_effect=preview)
    with pytest.raises(ValueError):
        await apply_command(rt,{'kind':'SetResearch','project':'geothermal'},token=rt.context_token,revision=revision)
    assert rt.current_plan.model_dump()==before
    assert not rt.manual_requests and rt.counters['actions']==0
    rt.store.close()


@pytest.mark.asyncio
async def test_chat_returns_native_research_refusal_without_model_rephrasing(tmp_path):
    from rimbot.planner import Planner
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    rt.chat_revision=1
    rt.chat=[{'kind':'human','revision':1,'text':'Set research to geothermal.'}]
    rt.inspect_native=AsyncMock(side_effect=ValueError('Missing prerequisite: Microelectronics. Nothing was written.'))
    rt.router.complete=AsyncMock(return_value=({'role':'assistant','tool_calls':[{
        'id':'research-request','type':'function','function':{'name':'SetResearch','arguments':'{"project":"geothermal"}'}}]},{}))
    await Planner(rt).play_bridge()
    rt.router.complete.assert_awaited_once()
    assert rt.chat[-1]['text']=='Research request blocked: Missing prerequisite: Microelectronics. Nothing was written.'
    assert rt.current_plan.revision==0 and not rt.manual_requests and rt.counters['actions']==0
    rt.store.close()


@pytest.mark.asyncio
async def test_chat_can_apply_both_explicit_policy_changes_without_another_model_turn(tmp_path):
    from rimbot.planner import Planner
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    query=rt.game.query
    async def observed(name,**args):
        if name=='home/colony_facts': return {'resources':{'Steel':200}}
        return await query(name,**args)
    rt.game.query=observed
    rt.chat_revision=1
    rt.chat=[{'kind':'human','revision':1,'text':'Reserve 100 steel and stop steel spending.'}]
    rt.router.complete=AsyncMock(return_value=({'role':'assistant','tool_calls':[
        {'id':'reserve','type':'function','function':{'name':'SetResourceReserve','arguments':'{"resource":"Steel","reserve":100}'}},
        {'id':'spending','type':'function','function':{'name':'ModifyResourcePolicy','arguments':'{"resource":"Steel","spending":"stop"}'}}]},{}))
    await Planner(rt).play_bridge()
    rt.router.complete.assert_awaited_once()
    assert rt.current_plan.control['resource_policy']['Steel']=={'reserve':100,'spending':'stop'}
    assert len(rt.manual_requests)==2 and rt.counters['actions']==0
    rt.store.close()


@pytest.mark.asyncio
async def test_food_target_does_not_discard_explicit_policies_in_same_response(tmp_path):
    from rimbot.planner import Planner
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    query=rt.game.query
    async def observed(name,**args):
        if name=='home/colony_facts': return {'success':True,'resources':{'Steel':200,'ComponentIndustrial':10},
            'policyResources':{'Steel':'steel','ComponentIndustrial':'component','ComponentSpacer':'advanced component'}}
        return await query(name,**args)
    rt.game.query=observed
    rt.chat_revision=1
    rt.chat=[{'kind':'human','revision':1,'text':'Set a 12-day food target, reserve 80 steel and stop spending components.'}]
    rt.router.complete=AsyncMock(return_value=({'role':'assistant','tool_calls':[
        {'id':'goal','type':'function','function':{'name':'CreateGoal','arguments':'{"goal":"EnsureFoodSupply","food_days":12}'}},
        {'id':'reserve','type':'function','function':{'name':'SetResourceReserve','arguments':'{"resource":"Steel","reserve":80}'}},
        {'id':'spending','type':'function','function':{'name':'ModifyResourcePolicy','arguments':'{"resource":"ComponentIndustrial","spending":"stop"}'}}]},{}))
    await Planner(rt).play_bridge()
    rt.router.complete.assert_awaited_once()
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].target['food_days']==12
    assert rt.current_plan.control['resource_policy']=={
        'Steel':{'reserve':80,'spending':'normal'},'ComponentIndustrial':{'reserve':0,'spending':'stop'}}
    assert len(rt.manual_requests)==2 and rt.counters['actions']==0
    rt.store.close()


@pytest.mark.asyncio
async def test_invalid_followup_preserves_original_pending_intent(tmp_path):
    rt=runtime(tmp_path); await rt.sync_identity();rt.batch=batch()
    command={'kind':'BuildRoom','intent_id':'bedroom','room':room_plan().steps[0].action.model_dump()}
    first=await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    before=rt.current_plan.model_dump()
    rt.chat_revision+=1
    command['room']['bounds']['x']=60
    rt.game.invoke=AsyncMock(return_value={'canPlace':False,'success':True})
    with pytest.raises(ValueError): await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before
    assert rt.current_plan.progress[first['step']].state=='pending'
    rt.store.close()


@pytest.mark.asyncio
async def test_room_followup_replaces_only_unissued_intent_and_preserves_history(tmp_path):
    rt=runtime(tmp_path); await rt.sync_identity();rt.batch=batch()
    command={'kind':'BuildRoom','intent_id':'bedroom','room':room_plan().steps[0].action.model_dump()}
    first=await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    rt.chat_revision+=1
    command['room']['bounds']['z']+=2
    second=await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    assert first['step']!=second['step'] and len(rt.current_plan.spec.steps)==1
    assert rt.current_plan.progress[first['step']].state=='cancelled'
    assert rt.current_plan.spec.steps[0].action.bounds.z==22
    assert rt.current_plan.control['player_intents']['bedroom']['step']==second['step']
    rt.current_plan.progress[second['step']].issued={'0':{'confirmed':True}}
    rt.chat_revision+=1
    command['room']['bounds']['z']+=2
    with pytest.raises(ValueError,match='issued'): await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    rt.store.close()


@pytest.mark.asyncio
async def test_shared_arbitration_rejects_player_and_autopilot_double_spend(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    rt.game.invoke=AsyncMock(side_effect=lambda name,args,**kw: native_reply(name,args,canPlace=True,costList=[{'defName':'WoodLog','count':60}],
        materials={'rows':[{'defName':'WoodLog','available':100}]}))
    first=PlanStep(id='auto',title='Auto',source='AUTOPILOT',action={'kind':'place_buildings',
        'placements':[{'def_name':'Bed','x':1,'z':1,'materials':['WoodLog']}]},completion_criteria='Bed exists')
    await rt.commit_strategy(CommitSteps(expected_revision=0,reason='Auto',steps=[first]).decision(rt.current_plan),
        actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
    second=first.model_copy(deep=True);second.id='player';second.source='PLAYER';second.action.placements[0].x=5
    with pytest.raises(ValueError,match='reservation'):
        await rt.commit_strategy(CommitSteps(expected_revision=1,reason='Player',steps=[second]).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
    assert len(rt.current_plan.spec.steps)==1
    rt.store.close()


@pytest.mark.asyncio
async def test_defense_only_component_policy_is_a_hard_shared_gate(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    rt.current_plan.control['resource_policy']={'ComponentIndustrial':{'reserve':0,'spending':'defense_only'}}
    rt.game.invoke=AsyncMock(side_effect=lambda name,args,**kw: footprint(args,canPlace=True,costList=[{'defName':'ComponentIndustrial','count':3}],
        materials={'rows':[{'defName':'ComponentIndustrial','available':10}]}))
    step=PlanStep(id='power',title='Power',source='PLAYER',purpose='production',action={'kind':'place_buildings',
        'placements':[{'def_name':'Generator','x':1,'z':1}]},completion_criteria='Built')
    proposal=CommitSteps(expected_revision=0,reason='Player',steps=[step]).decision(rt.current_plan)
    with pytest.raises(ValueError,match='policy'): await validate_allocations(proposal.plan,rt.current_plan,rt.game)
    proposal.plan.steps[0].purpose='defense'
    assert await validate_allocations(proposal.plan,rt.current_plan,rt.game)
    rt.store.close()



def test_policy_tool_uses_native_definition_catalog_even_when_stock_is_zero():
    from rimbot.player_commands import semantic_tools
    from rimbot.native_contracts import validate_arguments
    tool=next(t for t in semantic_tools({'WoodLog','ComponentIndustrial'})
        if t['function']['name']=='ModifyResourcePolicy')['function']
    validate_arguments(tool['name'],tool['parameters'],{'resource':'ComponentIndustrial','spending':'defense_only'})
    with pytest.raises(ValueError):
        validate_arguments(tool['name'],tool['parameters'],{'resource':'InventedResource'})


def test_resource_names_resolve_exact_native_labels_without_expanding_base_names():
    from rimbot.player_commands import resolve_resource,semantic_tools
    labels={'ComponentIndustrial':'component','ComponentSpacer':'advanced component','Steel':'steel'}
    assert resolve_resource('component',labels)=='ComponentIndustrial'
    assert resolve_resource('advanced component',labels)=='ComponentSpacer'
    assert resolve_resource('Steel',labels)=='Steel'
    choice=next(t['function']['parameters']['properties']['resource']['enum'] for t in semantic_tools(labels)
                if t['function']['name']=='ModifyResourcePolicy')
    assert choice==['advanced component','component','steel']
    with pytest.raises(ValueError,match='ambiguous'):
        resolve_resource('leather',{'LeatherA':'leather','LeatherB':'leather'})


@pytest.mark.asyncio
async def test_native_resource_label_is_persisted_as_exact_definition_id(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    query=rt.game.query
    async def observed(name,**args):
        if name=='home/colony_facts': return {'resources':{'ComponentIndustrial':10},
            'policyResources':{'ComponentIndustrial':'component','ComponentSpacer':'advanced component'}}
        return await query(name,**args)
    rt.game.query=observed
    result=await apply_command(rt,{'kind':'ModifyResourcePolicy','resource':'component','spending':'stop'},
        token=rt.context_token,revision=rt.chat_revision)
    assert result['resource']=='ComponentIndustrial'
    assert rt.current_plan.control['resource_policy']=={'ComponentIndustrial':{'reserve':0,'spending':'stop'}}
    rt.store.close()


@pytest.mark.asyncio
async def test_archived_player_intent_is_observed_without_replay(tmp_path):
    from rimbot.plan_archive import bind_archive
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    request={'kind':'BuildRoom','intent_id':'bedroom','room':{'kind':'build_room_shell',
        'bounds':{'x':10,'z':10,'width':5,'height':5},'wall_def':'Wall','door_def':'Door',
        'materials':['WoodLog'],'entrance':'south'}}
    step=PlanStep(id='player-bedroom',title='Bedroom',source='PLAYER',action=request['room'],completion_criteria='Native room')
    rt.store.archive_and_set(rt.colony,'archive-test',{}, {step.id:{'step':step.model_dump(),
        'progress':StepProgress(state='complete',issued={'0':{'confirmed':True}}).model_dump(),'costs':None}})
    rt.current_plan.control.update(archived_action_count=1,player_intents={'bedroom':{'step':step.id,'request':request}})
    bind_archive(rt.current_plan,rt.store,rt.colony)
    result=await apply_command(rt,request,token=rt.context_token,revision=rt.chat_revision)
    assert result=={'existing_step':step.id,'state':'complete','archived':True}
    assert not rt.manual_requests and not rt.current_plan.spec.steps
    changed=deepcopy(request);changed['room']['entrance']='north'
    with pytest.raises(ValueError,match='completed and archived'):
        await apply_command(rt,changed,token=rt.context_token,revision=rt.chat_revision)
    rt.store.close()

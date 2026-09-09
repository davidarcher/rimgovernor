from unittest.mock import AsyncMock
import pytest
from rimbot.player_commands import apply_command
from rimbot.colony_plan import ColonyPlan, CommitSteps, PlanStep, StepProgress
from rimbot.resource_accounting import validate_allocations
from rimbot.resource_accounting import validate_execution_costs
from test_strategic_architecture import runtime, batch, room_plan


@pytest.mark.asyncio
async def test_manual_chat_dispatches_only_current_player_order_without_resuming_clock(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{'set':{'type':'string'},
        'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}},'additionalProperties':False})
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
    result=await apply_command(rt,{'kind':'SetResearch','project':'GeothermalPower'},token=rt.context_token,revision=rt.chat_revision)
    step=rt.current_plan.spec.steps[0]
    assert step.source=='PLAYER' and step.action.tool=='home/research'
    assert step.action.arguments=={'set':'GeothermalPower','dryRun':False,'watch':False}
    assert rt.current_plan.progress[result['step']].state=='pending'
    assert list(rt.current_plan.ready())==[step]
    assert rt.counters['actions']==0
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
    rt.game.invoke=AsyncMock(return_value={'canPlace':True,'costList':[{'defName':'WoodLog','count':60}],
        'materials':{'rows':[{'defName':'WoodLog','available':100}]}})
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
    rt.game.invoke=AsyncMock(return_value={'canPlace':True,'costList':[{'defName':'ComponentIndustrial','count':3}],
        'materials':{'rows':[{'defName':'ComponentIndustrial','available':10}]}})
    step=PlanStep(id='power',title='Power',source='PLAYER',purpose='production',action={'kind':'place_buildings',
        'placements':[{'def_name':'Generator','x':1,'z':1}]},completion_criteria='Built')
    proposal=CommitSteps(expected_revision=0,reason='Player',steps=[step]).decision(rt.current_plan)
    with pytest.raises(ValueError,match='policy'): await validate_allocations(proposal.plan,rt.current_plan,rt.game)
    proposal.plan.steps[0].purpose='defense'
    assert await validate_allocations(proposal.plan,rt.current_plan,rt.game)
    rt.store.close()

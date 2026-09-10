from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyGoal,PlanSpec,PlanStep,StepProgress
from rimbot.hunting import HuntingRefused
from rimbot.native_contracts import NativeNotDispatched
from test_hunting_screen import pawn
from test_strategic_architecture import runtime


def hunt_runtime(tmp_path):
    rt=runtime(tmp_path)
    step=PlanStep(id='EnsureFoodSupply-0-hunt-prey-0',title='Hunt',goal_id='EnsureFoodSupply',source='AUTOPILOT',
        action={'kind':'native_operation','tool':'rimworld/apply_architect_designator',
            'arguments':{'designatorId':'hunt','x':0,'z':0,'dryRun':False}},completion_criteria='Designated')
    rt.current_plan.spec=PlanSpec(steps=[step]);rt.current_plan.progress[step.id]=StepProgress()
    rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2,
        evidence={'hunting_targets':{step.id:{'prey':'prey','anchor':{'x':0,'z':0},'signature':step.signature()}}})
    return rt,step


@pytest.mark.asyncio
@pytest.mark.parametrize('change',['none','moved','predator','unknown','designated','legacy','direction','tick','unconfirmed'])
async def test_hunt_rechecks_before_native_write(tmp_path,change):
    rt=runtime(tmp_path);await rt.sync_identity()
    configured,step=hunt_runtime(tmp_path/'plan')
    rt.current_plan=configured.current_plan;configured.store.close()
    rt.mode='automate';token=rt.context_token;direction=rt.chat_revision
    prey=pawn('prey');predator=pawn('predator',25);predator['predator']=True
    wildlife=[prey]
    if change=='moved':prey['position']['x']=1
    if change=='predator':wildlife.append(predator)
    if change=='unknown':prey['predator']=None
    if change=='designated':prey['animals']['designations']['hunt']=True
    if change=='legacy':rt.current_plan.colony_goals['EnsureFoodSupply'].evidence.clear()
    query=rt.game.query;reads=0
    async def observed(name,**args):
        nonlocal reads
        if name=='home/status':
            reads+=1
            return {'time':{'paused':True,'ticksGame':100+int(change=='tick' and reads>1)}}
        if name=='home/list_pawns':
            if change=='direction':rt.chat_revision+=1
            if change=='none' and rt.game.invoke.await_count:
                prey['animals']['designations']['hunt']=True
            return {'pawns':wildlife}
        return await query(name,**args)
    rt.game.query=observed;rt.game.invoke=AsyncMock(return_value={'success':True})
    args=dict(expected_token=token,expected_revision=direction,expected_plan_revision=rt.current_plan.revision,
              expected_step_id=step.id,reconcile=False)
    if change=='none':
        await rt.native(step.action.tool,step.action.arguments,**args)
        assert sum(c.args[0]==step.action.tool for c in rt.game.invoke.await_args_list)==1
    elif change=='unconfirmed':
        with pytest.raises(ValueError,match='not confirmed'):await rt.native(step.action.tool,step.action.arguments,**args)
        assert sum(c.args[0]==step.action.tool for c in rt.game.invoke.await_args_list)==1
    else:
        with pytest.raises(HuntingRefused):await rt.native(step.action.tool,step.action.arguments,**args)
        rt.game.invoke.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_known_prewrite_refusal_clears_uncertain_marker_and_blocks_retry(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity()
    configured,step=hunt_runtime(tmp_path/'plan')
    rt.current_plan=configured.current_plan;configured.store.close()
    rt.mode='automate';rt.handled_revision=rt.chat_revision
    rt.game.describe=AsyncMock(return_value={'properties':{}})
    rt.native=AsyncMock(side_effect=HuntingRefused('Predator arrived; no designation sent'))
    await rt.hands.advance(rt)
    progress=rt.current_plan.progress[step.id]
    assert progress.state=='blocked' and progress.failure.code=='hunting_precondition'
    assert not progress.issued and not progress.failure.retryable
    await rt.hands.advance(rt)
    rt.native.assert_awaited_once()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('known',[True,False])
async def test_direction_change_preserves_only_genuinely_uncertain_writes(tmp_path,known):
    rt=runtime(tmp_path);await rt.sync_identity()
    configured,step=hunt_runtime(tmp_path/'plan')
    rt.current_plan=configured.current_plan;configured.store.close()
    rt.mode='automate';rt.handled_revision=rt.chat_revision
    rt.game.describe=AsyncMock(return_value={'properties':{}})
    async def interrupted(*args,**kwargs):
        rt.chat_revision+=1
        raise NativeNotDispatched('No requested action sent') if known else ValueError('Native receipt lost')
    rt.native=AsyncMock(side_effect=interrupted)
    await rt.hands.advance(rt)
    progress=rt.current_plan.progress[step.id]
    if known:
        assert progress.state=='pending' and not progress.issued
    else:
        assert progress.issued=={'0':{'confirmed':False}}
        rt.handled_revision=rt.chat_revision
        await rt.hands.advance(rt)
        assert progress.failure.code=='uncertain_write'
    rt.mode='manual';rt.handled_revision=rt.chat_revision
    await rt.hands.advance(rt)
    rt.native.assert_awaited_once()
    rt.store.close()

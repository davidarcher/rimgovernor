from rimbot.work_allocation import allocate,plan
from rimbot.semantic_models import WorkPolicy
from unittest.mock import AsyncMock
import pytest


DEFS=[{'def_name':'BuildMod','relevant_skills':['CraftMod']},{'def_name':'CareMod','relevant_skills':['MedicineMod']}]
def pawn(identity,skill,priority=0,disabled=False,downed=False,doctor=0):
    return {'colonist':{'id':identity},'colonist_medical_info':{'is_dead':False,'is_downed':downed},
      'colonist_work_info':{'skills':[{'name':n,'level':skill,'passion':0,'totally_disabled':False,'permanently_disabled':False} for n in ('CraftMod','MedicineMod')],
      'work_priorities':[{'work_type':n,'priority':p,'is_totally_disabled':disabled} for n,p in [('BuildMod',priority),('CareMod',doctor)]]}}
def policy(**kw):return WorkPolicy(coverage=[{'work_type':'BuildMod','workers':1,'priority':2}],**kw)


def test_native_skills_choose_worker_and_disabled_or_downed_workers_are_excluded():
    selected,changes,blocked=allocate(policy(),DEFS,[pawn(1,20,disabled=True),pawn(2,20,downed=True),pawn(3,10),pawn(4,5)])
    assert not blocked and selected[0]['pawn_id']==3
    assert changes==[{'id':3,'work':'BuildMod','priority':2}]


def test_existing_coverage_is_stable_and_sole_protected_provider_is_preserved():
    selected,changes,blocked=allocate(policy(),DEFS,[pawn(1,20),pawn(2,5,priority=1)])
    assert selected[0]['pawn_id']==2 and not changes and not blocked
    selected,changes,blocked=allocate(policy(protect=['CareMod']),DEFS,[pawn(1,20,doctor=1),pawn(2,5)])
    assert selected[0]['pawn_id']==2 and changes[0]['id']==2


def test_shortfall_missing_skills_and_unknown_work_never_produce_partial_changes():
    p=WorkPolicy(coverage=[{'work_type':'BuildMod','workers':2}])
    assert allocate(p,DEFS,[pawn(1,10)])[1]==[]
    assert allocate(p,DEFS,[pawn(1,10)])[2]
    no_skills=pawn(1,10);no_skills['colonist_work_info']['skills']=[]
    assert allocate(policy(),DEFS,[no_skills])[2]
    with pytest.raises(ValueError):allocate(policy(),[],[pawn(1,10)])
    assert allocate(policy(),DEFS,[pawn(1,10)],{1})[2]


async def test_project_execution_uses_policy_without_executor_model(colony,monkeypatch):
    from rimbot.semantic import execute_projects,retain_project
    from rimbot.semantic_models import WorkObjective
    from rimbot.contracts import Proposal
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    p=retain_project(rt.memory,'Infrastructure',WorkObjective(kind='work_assignment',outcome='Cover construction',work_policy=policy(),success_signals=['Coverage present']))
    rt.planner.ask=AsyncMock(side_effect=AssertionError('Policy execution must not invoke a model'))
    allocator=AsyncMock(return_value=Proposal(summary='Coverage present'))
    monkeypatch.setattr('rimbot.work_allocation.plan',allocator)
    await execute_projects(rt,{},[p])
    allocator.assert_awaited_once();rt.planner.ask.assert_not_called()
    assert p['status']=='orders_verified'


async def test_policy_produces_normal_verified_native_orders_without_model(colony):
    rt,_=colony;rt.observation['pawns']=[pawn(1,10)]
    async def call(name,args,**kwargs):return {'work_type_defs':DEFS} if name=='get_def_all' else {'use_work_priorities':False}
    rt.api.call=call;rt.planner.ask=AsyncMock(side_effect=AssertionError('No LLM allocation'))
    project={'project_id':'p','work_policy':policy().model_dump()}
    batch=await plan(rt,project,{})
    assert [a.endpoint for a in batch.actions]==['post_work_settings','post_colonists_work_priority']
    assert batch.actions[-1].arguments['priorities'][0]['id']==1
    rt.planner.ask.assert_not_called()

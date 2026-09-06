import pytest
from rimbot.semantic import semantic_review,retain_project
from rimbot.semantic_models import WorkObjective,ObjectiveProposal,ObjectiveDecision
from rimbot.contracts import Proposal,Action

def setup(rt):
    rt.mode='automate';rt.cycle_generation=rt.generation
    p=retain_project(rt.memory,'Infrastructure',WorkObjective(kind='supply_access',outcome='Allow timber',success_signals=['Timber allowed']))
    rt.memory['last_admin_day']=(rt.last_tick or 0)//60000
    return p

async def test_routine_review_directly_executes_owned_project(colony):
    rt,game=colony;p=setup(rt);calls=[]
    async def ask(role,context,contract,thinking):
        calls.append(role)
        assert contract is Proposal and context['project_owner']=='Infrastructure'
        return Proposal(summary='Allow timber',actions=[Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})])
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert calls==['Executor:supply_access'] and game.writes==['things/set-forbidden']
    assert p['status']=='orders_verified'

async def test_conflict_requests_admin_without_executing(colony):
    rt,game=colony;p=setup(rt)
    async def ask(*args):return Proposal(summary='Need priority choice',escalation_reason='Two approved projects require the same scarce supply')
    rt.planner.ask=ask
    await semantic_review(rt,{},[])
    assert rt.memory['admin_requested'] and p['status']=='needs_review' and not game.writes

async def test_daily_admin_kept_project_runs_without_reproposal(colony):
    rt,game=colony;p=setup(rt);rt.memory['last_admin_day']=-1;calls=[]
    async def ask(role,context,contract,thinking):
        calls.append(role)
        if contract is ObjectiveProposal:return ObjectiveProposal(summary='Existing project sufficient')
        if contract is ObjectiveDecision:return ObjectiveDecision(response='Continue existing work',keep_projects=[p['project_id']])
        return Proposal(summary='No new order needed')
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert calls[-1]=='Executor:supply_access' and len(calls)==3
    assert rt.memory['last_admin_day']==(rt.last_tick or 0)//60000

async def test_identical_unknown_order_is_not_resent(colony):
    rt,game=colony;setup(rt)
    action=Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    rt.memory['work']=[{'id':'pending','status':'unknown','action':action.model_dump()}]
    await rt.execute(action,'Executor:supply_access')
    assert not game.writes and len(rt.memory['work'])==1

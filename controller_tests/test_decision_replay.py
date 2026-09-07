from rimbot.store import Store
from rimbot.decision_replay import replay_budget


def request(content='Observe'):
    return {'format_version':1,'messages':[{'role':'user','content':content}],
            'tools':[],'context_limit':16384,'output_tokens':4096}


def test_checkpoint_is_exact_isolated_from_history_and_persists(tmp_path):
    path=tmp_path/'trace.sqlite';store=Store(path)
    source=request('Colonist: José; 木材')
    identity=store.decision('colony-a','Executor:construction',source)
    source['messages'][0]['content']='Changed later'
    assert store.history('colony-a',include_diagnostics=True)==[]
    store.finish_decision(identity,{'status':'returned','reply':{'content':'Inspect first'}})
    store.close();store=Store(path)
    saved=store.read_decision(identity)
    assert saved['request']['messages'][0]['content']=='Colonist: José; 木材'
    assert saved['colony']=='colony-a' and saved['result']['status']=='returned'
    assert replay_budget(saved['request'])['fits']
    store.close()


def test_checkpoint_retention_is_bounded_across_colonies(tmp_path):
    import pytest
    store=Store(tmp_path/'trace.sqlite')
    ids=[store.decision(str(i),'Architect',request()) for i in range(70)]
    assert store.db.execute('SELECT COUNT(*) FROM decisions').fetchone()[0]==64
    with pytest.raises(KeyError):store.read_decision(ids[0])
    assert store.read_decision(ids[-1])['result'] is None
    store.close()


def test_budget_failure_reproduces_without_model_or_game():
    source=request()
    source['messages']=[{'role':'system','content':'X'*30000}]
    assert replay_budget(source)==replay_budget(source)
    assert not replay_budget(source)['fits']
    assert 'cannot fit safely' in replay_budget(source)['error']


async def test_planner_failure_records_original_input_and_links_diagnostic(colony):
    from unittest.mock import AsyncMock
    from rimbot.model import ModelError
    from rimbot.contracts import Proposal
    import pytest
    rt,_=colony
    model=rt.model_for_role('Survival')
    model.complete=AsyncMock(side_effect=ModelError('Request cannot fit safely: test'))
    with pytest.raises(ModelError):await rt.planner.ask('Survival',{'assigned_task':'Check food'},Proposal,False)
    event=next(e for e in rt.store.history(rt.colony,include_diagnostics=True) if e['kind']=='model_failure')
    saved=rt.store.read_decision(event['decision_id'])
    assert saved['request']['messages']==model.complete.call_args.args[0]
    assert saved['request']['tools']==model.complete.call_args.args[1]
    assert saved['result']['status']=='failed'

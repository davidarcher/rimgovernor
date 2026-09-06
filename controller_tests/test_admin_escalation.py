import pytest
from rimbot.semantic import arbitrate_objectives
from rimbot.semantic_models import ObjectiveDecision
from rimbot.model import ModelError

@pytest.mark.parametrize('trigger',['uncertainty','failure'])
async def test_admin_escalates_once_before_applying_anything(colony,trigger):
    rt,game=colony
    await rt.configure(rt.settings.model_copy(update={'manager_model':'qwen3.5-4b'}))
    calls=[]
    async def ask(role,context,contract,thinking):
        calls.append(role)
        assert not game.writes and not rt.memory['projects']
        if len(calls)==1:
            if trigger=='failure':raise ModelError('Invalid decision')
            return ObjectiveDecision(response='Need review',escalation_reason='Conflicting projects cannot be reconciled reliably')
        assert role=='Administrator: escalated review' and thinking is True
        assert context['escalation']
        return ObjectiveDecision(response='No changes needed')
    rt.planner.ask=ask
    assert (await arbitrate_objectives(rt,{'projects':[],'proposals':[]})).response=='No changes needed'
    assert len(calls)==2
    events=rt.store.history(rt.colony,100)
    assert any(e['kind']=='escalation' and e['to_model']==rt.settings.model for e in events)

async def test_admin_confident_decision_does_not_escalate(colony):
    rt,_=colony;calls=[]
    async def ask(*args):calls.append(1);return ObjectiveDecision(response='No changes')
    rt.planner.ask=ask
    await arbitrate_objectives(rt,{'projects':[],'proposals':{}})
    assert len(calls)==1

async def test_escalation_cannot_authorize_changes_or_recurse(colony):
    rt,_=colony
    with pytest.raises(ValueError,match='cannot also approve'):
        rt.planner.validate_submission('Administrator',ObjectiveDecision(response='x',accepted=['a'],escalation_reason='Uncertain'),{})
    await rt.configure(rt.settings.model_copy(update={'manager_model':'qwen3.5-4b'}))
    calls=[]
    async def ask(*args):calls.append(1);return ObjectiveDecision(response='x',escalation_reason='Still uncertain')
    rt.planner.ask=ask
    with pytest.raises(ModelError,match='remained uncertain'):
        await arbitrate_objectives(rt,{'projects':[],'proposals':{}})
    assert len(calls)==2

from types import SimpleNamespace
from rimbot.bridge_models import BridgeSupply
from rimbot.strategic_state import observed_supplies


def supply(name, owned=100, allowed=20):
    return BridgeSupply(def_name=name,label=name,owned_units=owned,
        owned_unforbidden_units=allowed,forbidden_units_all_owners=900,
        stockpiled_units_all_owners=800,fogged_units_all_owners=700,trader_units=600)


def test_ownership_counts_do_not_include_trader_or_cave_stock():
    batch=SimpleNamespace(summary=SimpleNamespace(end_tick=40,supplies=[supply('WoodLog')]))
    result=observed_supplies(batch)
    assert result['supplies']['items']==[dict(def_name='WoodLog',owned_units=100,
        allowed_units=20,forbidden_owned_units=80)]
    assert result['observed_tick']==40
    batch.summary.supplies[0].owned_unforbidden_units=100
    assert observed_supplies(batch)['supplies']['items'][0]['forbidden_owned_units']==0


def test_bounded_inventory_reports_omissions_and_unknown_observation():
    batch=SimpleNamespace(summary=SimpleNamespace(end_tick=0,
        supplies=[supply(f'Def{i:02}') for i in reversed(range(40))]))
    result=observed_supplies(batch)['supplies']
    assert len(result['items'])==32 and result['omitted']==8
    assert result['items'][0]['def_name']=='Def00'
    assert observed_supplies(None) is None

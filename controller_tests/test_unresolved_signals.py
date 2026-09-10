from rimgovernor.strategic_state import StrategicState


def test_acknowledging_an_event_does_not_permanently_silence_hunger(monkeypatch):
    value={'tick':1,'people':{'count':1,'downed':[],'dead':[],'bleeding':[],
        'needs_tend':[],'mood':{'pawn':.8},'food_need':{'pawn':.1}},
        'resources':{'construction_deficit':[],'allowed_units_by_def':{}},
        'power':[],'threats':{},'alerts':[],'space':{},'warnings':[]}
    monkeypatch.setattr('rimgovernor.strategic_state.features',lambda _:dict(value))
    state=StrategicState();state.update(None);state.decided()
    value['tick']=5000;state.update(None)
    assert not state.pending
    value['tick']=5001;state.update(None)
    assert any(e['kind']=='urgent.unresolved' for e in state.pending)
    state.update(None)
    assert sum(e['kind']=='urgent.unresolved' for e in state.pending)==1
    state.decided();value['people']['food_need']['pawn']=.8
    value['tick']=10001;state.update(None)
    assert not any(e['kind']=='urgent.unresolved' for e in state.pending)

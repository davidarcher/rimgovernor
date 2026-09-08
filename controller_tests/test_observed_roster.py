from types import SimpleNamespace
from rimbot.bridge_models import BridgePawn
from rimbot.strategic_state import observed_roster


def pawn(index):
    return BridgePawn(thing_id=f'Thing_Human{index}', name=f'Pawn {index}',
        position={'x':140+index,'z':120},job=None,drafted=False,downed=False,dead=False,
        mood=None,food=None,rest=None,armed=None,primary_weapon=None,needs_tend=None,bleeding=None)


def test_roster_is_current_bounded_and_preserves_unknowns():
    batch = SimpleNamespace(summary=SimpleNamespace(end_tick=123,pawns=[pawn(i) for i in range(20)]))
    result = observed_roster(batch)
    assert result['observed_tick'] == 123
    assert len(result['pawns']['items']) == 16 and result['pawns']['omitted'] == 4
    first = result['pawns']['items'][0]
    assert first['position'] == {'x':140,'z':120}
    assert first['job'] is None and first['primary_weapon'] is None
    batch.summary.pawns[0].position.x = 150
    assert observed_roster(batch)['pawns']['items'][0]['position']['x'] == 150
    assert first['position']['x'] == 140


def test_missing_observation_is_unknown_not_empty_colony():
    assert observed_roster(None) is None

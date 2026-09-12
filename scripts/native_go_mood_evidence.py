"""Compare durable mood planning with native inputs and the Python policy."""
import hashlib
import math

from rimgovernor.mood_control import assess


def mood_reference(people, forecasts):
    return {pawn: state for pawn, state in assess(people, forecasts, {}).items() if state['active']}


def audit_mood_review(active, reference, setup):
    review = active['review']
    states = {s['Pawn']['ID']: s for s in (review.get('Mood') or {}).get('States', [])}
    methods = {m['Pawn']: m for m in review.get('MoodMethods', [])}
    assert set(states) == set(methods) == set(reference)
    assert setup['pawn'] in states, 'Fixture mood risk did not reach a maintained goal'
    for pawn, expected in reference.items():
        state, method = states[pawn], methods[pawn]
        goal_id = 'EnsureMood-' + pawn if len(pawn.encode()) <= 210 else 'EnsureMoodHash-' + hashlib.sha256(pawn.encode()).hexdigest()[:32]
        goal = active['goals'][goal_id]
        assert state['Active'] and not state['Missing']
        assert goal['Need'] == 'deficit' and goal['Priority'] == expected['priority']
        for key, source in [('Mood', 'mood'), ('Threshold', 'threshold')]:
            assert math.isclose(state['Pawn'][key], expected[source], abs_tol=1e-6)
        assert [c['Need'] for c in state['Causes'] or []] == [c['need'] for c in expected['causes']]
        for actual, cause in zip(state['Causes'] or [], expected['causes']):
            assert actual['Level'] == cause['level'] or math.isclose(actual['Level'], cause['level'], abs_tol=1e-6)
        assert method['MoodBenefit'] is None, 'Predicted mood benefit substituted for native evidence'
    chosen = methods[setup['pawn']]
    scenario = setup['scenario']
    reason = {'food': 'native_need_relief', 'forced': 'pawn_unavailable_or_player_work', 'mental': 'native_mental_break'}[scenario]
    assert chosen['Reason'] == reason
    if scenario == 'food':
        assert chosen['Need'] == 'food' and chosen['Target'] == .5 and chosen['NeedBenefit'] > 0
    else:
        assert not chosen['Need'], 'Protected/native mental work became a relief proposal'
    return {'states': states, 'methods': methods}

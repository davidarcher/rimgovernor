from rimgovernor.colony_plan import ColonyPlan, ColonyGoal, PlanStep, StepProgress
from rimgovernor.construction_ownership import owned_buildings


def scenario():
    step = PlanStep(id='wall', title='Wall', source='AUTOPILOT', goal_id='shelter',
        completion_criteria='Native construction', action=dict(kind='place_buildings',
            placements=[dict(def_name='Wall', x=10, z=20, materials=['WoodLog'])]))
    plan = ColonyPlan(colony_goals={'shelter': ColonyGoal(priority_class=2)})
    plan.spec.steps.append(step)
    plan.progress['wall'] = StepProgress(state='complete', issued={'0': dict(confirmed=True,
        outcome='placed', placed_thing_id='blueprint', stuff='WoodLog')})
    row = dict(origin='blueprint', current='built-wall', stage='built', present=True, blocker=None,
               definition='Wall', stuff='WoodLog', x=10, z=20, rotation=0)
    f = dict(tick=10, upkeep=dict(version=1, tick=10, errors={}, construction=[row]))
    return plan, f, row


def test_exact_native_transition_and_confirmed_autonomous_receipt_establish_ownership():
    plan, f, row = scenario()
    assert owned_buildings(plan, f)['built-wall']['origin'] == 'blueprint'
    row['present'] = False  # Identical replacement at the same coordinates is independent.
    assert owned_buildings(plan, f) == {}
    row['present'] = True
    row['blocker'] = 'Ambiguous native completion'
    assert owned_buildings(plan, f) == {}


def test_reused_player_blueprints_and_uncertain_or_cancelled_work_never_transfer_ownership():
    plan, f, _ = scenario()
    receipt = plan.progress['wall'].issued['0']
    receipt['outcome'] = 'already_present'
    assert owned_buildings(plan, f) == {}
    receipt['outcome'], receipt['confirmed'] = 'placed', False
    assert owned_buildings(plan, f) == {}
    receipt['confirmed'] = True
    plan.colony_goals['shelter'].source = 'PLAYER'
    assert owned_buildings(plan, f) == {}
    plan.colony_goals['shelter'].source = 'AUTOPILOT'
    plan.colony_goals['shelter'].cancelled = True
    assert owned_buildings(plan, f) == {}


def test_missing_or_stale_lineage_remains_unknown():
    plan, f, _ = scenario()
    f['upkeep']['tick'] = 9
    assert owned_buildings(plan, f) is None
    f['upkeep']['tick'] = 10
    f['upkeep']['construction'].append(dict(f['upkeep']['construction'][0]))
    assert owned_buildings(plan, f) is None

from copy import deepcopy
from unittest.mock import AsyncMock

import pytest

from rimbot.world_progression import caravan_outcome, quest_outcome, survival_assessment


SCOPE = dict(colonyId='colony', loadToken='load', mapId=1)


def observation():
    return dict(SCOPE, success=True, complete=True, ticksGame=101,
        caravans=[dict(id='Caravan1', tile=20, destination=20, moving=False,
            pawns=[dict(thingId='pawn1', dead=False, downed=False)])],
        quests=[dict(id='Quest1', state='Ongoing')])


def outcome(value, **kwargs):
    return caravan_outcome(value, scope=SCOPE, pawn_ids=['pawn1'], destination=20,
        issued_tick=100, **kwargs)['state']


def quest(value):
    return quest_outcome(value, 'Quest1', scope=SCOPE, issued_tick=100)


def test_receipts_missing_pawns_and_ongoing_quests_are_not_completion():
    assert outcome(dict(success=True, accepted=True)) == 'unknown'
    value = observation()
    value['caravans'] = []
    assert outcome(value) == 'waiting'
    assert quest(value) == 'waiting'
    value['quests'][0]['state'] = 'EndedSuccess'
    assert quest(value) == 'complete'


@pytest.mark.parametrize('field,value', [('colonyId', 'other'), ('loadToken', 'other'), ('mapId', 2)])
def test_scope_changes_invalidate(field, value):
    observed = observation()
    observed[field] = value
    assert outcome(observed) == 'invalidated'
    assert quest(observed) == 'invalidated'


@pytest.mark.parametrize('tick', [None, True, 100, 99, float('nan')])
def test_stale_or_invalid_ticks_cannot_complete(tick):
    observed = observation()
    observed['ticksGame'] = tick
    assert outcome(observed) == 'unknown'
    assert quest(observed) == 'unknown'


def test_arrival_requires_exact_healthy_membership_and_stopped_destination():
    observed = observation()
    assert outcome(observed, caravan_id='Caravan1') == 'arrived'
    assert outcome(observed, caravan_id='Replacement') == 'invalidated'
    observed['caravans'][0]['moving'] = True
    assert outcome(observed) == 'travelling'
    observed['caravans'][0]['pawns'][0]['downed'] = True
    assert outcome(observed) == 'blocked'
    observed = observation()
    extra = deepcopy(observed['caravans'][0]['pawns'][0])
    extra['thingId'] = 'unexpected'
    observed['caravans'][0]['pawns'].append(extra)
    assert outcome(observed) == 'blocked'


def test_transport_truncation_overrides_complete_flag():
    observed = observation()
    observed['operation'] = {'ResultWasTruncated': True}
    assert outcome(observed) == 'unknown'
    assert quest(observed) == 'unknown'


@pytest.mark.parametrize('state', ['EndedFailed', 'EndedOfferExpired', 'EndedInvalid', 'EndedUnknownOutcome'])
def test_quest_terminal_failure_is_not_success(state):
    observed = observation()
    observed['quests'][0]['state'] = state
    assert quest(observed) == 'blocked'


def test_unknown_food_and_warm_weather_do_not_certify_winter_readiness():
    value = survival_assessment(dict(colonists=1, indoorSleepingCapacity=1,
        sleepingTemperatureMin=20, sleepingTemperatureMax=22,
        foodStorage=True, outdoorTemperature=18))
    assert value['checks']['food_reserve'] is None
    assert value['checks']['cold_exposure_observed'] is False
    assert value['winter_readiness_observed'] is False


@pytest.mark.parametrize('days', [0, -1, True, float('inf')])
def test_invalid_winter_horizons_are_refused(days):
    with pytest.raises(ValueError):
        survival_assessment({}, reserve_days=days)


@pytest.mark.asyncio
@pytest.mark.parametrize('accepted', [True, False])
async def test_quest_request_previews_and_joins_shared_plan_only_if_eligible(tmp_path, accepted):
    from test_strategic_architecture import runtime, batch
    from rimbot.player_commands import apply_command
    rt = runtime(tmp_path)
    await rt.sync_identity()
    rt.batch = batch()
    rt.game.describe = AsyncMock(return_value={'type': 'object'})
    rt.game.invoke = AsyncMock(return_value={'success': True, 'accepted': accepted, 'reason': 'Native requirements'})
    request = dict(kind='AcceptQuest', quest_id='Quest1', pawn_id='Pawn1', reward_choice=0)
    try:
        if accepted:
            result = await apply_command(rt, request, token=rt.context_token, revision=rt.chat_revision)
            step = rt.current_plan.spec.steps[0]
            assert step.action.tool == 'home/accept_quest'
            assert step.action.arguments['dryRun'] is False
            assert step.action.arguments['questId'] == 'Quest1'
            assert rt.current_plan.progress[result['step']].state == 'pending'
            assert rt.counters['actions'] == 0
        else:
            with pytest.raises(ValueError, match='Native requirements'):
                await apply_command(rt, request, token=rt.context_token, revision=rt.chat_revision)
            assert not rt.current_plan.spec.steps
    finally:
        rt.store.close()


def caravan_plan():
    from rimbot.colony_plan import ColonyPlan, PlanStep, StepProgress
    plan = ColonyPlan()
    plan.spec.steps = [PlanStep(id='trip', title='Trip', source='PLAYER', completion_criteria='Loaded departure',
        action=dict(kind='native_operation', tool='home/caravan', completion='caravan_departed',
            arguments=dict(SCOPE, action='form', pawnIds='Thing_pawn1', cargoIds='food', counts='60', destination=20, dryRun=False),
            caravan_target=dict(pawn_ids=['Thing_pawn1'], destination=20, cargo={'Pemmican': 60})))]
    plan.progress['trip'] = StepProgress(state='waiting', issued={'0': {'confirmed': True, 'issued_tick': 100}})
    plan.control['costs'] = {'trip': {'0': {'Pemmican': 60}}}
    return plan


@pytest.mark.parametrize('state', ['waiting', 'cancelled', 'blocked'])
def test_confirmed_assembly_holds_cargo_until_observed_departure(state):
    from rimbot.resource_accounting import execution_reservations
    from rimbot.production_policy import production_budgets
    plan = caravan_plan()
    plan.progress['trip'].state = state
    assert execution_reservations(plan, 'other') == {'Pemmican': 60}
    assert production_budgets(plan)[0] == {'Pemmican': 60}
    plan.progress['trip'].issued['0']['cargo_departed'] = True
    assert execution_reservations(plan, 'other') == {}
    assert production_budgets(plan)[0] == {}


def test_nonworld_native_serialization_preserves_prior_fingerprint():
    from rimbot.colony_plan import NativeOperation
    action = NativeOperation(tool='home/research', arguments={'set': 'A', 'dryRun': False})
    assert action.model_dump_json() == '{"kind":"native_operation","tool":"home/research","arguments":{"set":"A","dryRun":false},"completion":"native_receipt"}'


@pytest.mark.asyncio
async def test_loaded_departure_completes_shared_action_without_replaying_write():
    from types import SimpleNamespace
    from unittest.mock import Mock
    from rimbot.world_progression import reconcile_world
    plan = caravan_plan()
    value = observation()
    pawn = value['caravans'][0]['pawns'][0]
    pawn.update(thingId='Thing_pawn1', inventory=[{'defName': 'Pemmican', 'count': 60}])
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=AsyncMock(return_value=value)), signal=Mock())
    await reconcile_world(rt)
    assert plan.progress['trip'].state == 'complete'
    assert plan.progress['trip'].issued['0']['cargo_departed'] is True
    assert plan.progress['trip'].issued['0']['observed_cargo'] == {'Pemmican': 60}
    assert rt.game.query.await_count == 1


@pytest.mark.asyncio
async def test_uncertain_departure_releases_observed_cargo_without_reviving_blocked_work():
    from types import SimpleNamespace
    from unittest.mock import Mock
    from rimbot.world_progression import reconcile_world
    from rimbot.resource_accounting import execution_reservations
    plan = caravan_plan()
    progress = plan.progress['trip']
    progress.state = 'blocked'
    progress.issued['0']['confirmed'] = False
    value = observation()
    value['caravans'][0]['pawns'][0].update(thingId='Thing_pawn1',
        inventory=[{'defName': 'Pemmican', 'count': 60}])
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=AsyncMock(return_value=value)), signal=Mock())
    assert execution_reservations(plan, 'other') == {'Pemmican': 60}
    await reconcile_world(rt)
    assert progress.state == 'blocked'
    assert execution_reservations(plan, 'other') == {}
    rt.signal.assert_not_called()


@pytest.mark.asyncio
async def test_cargo_reserve_policy_rejects_admission():
    from rimbot.colony_plan import ColonyPlan
    from rimbot.resource_accounting import validate_allocations
    from types import SimpleNamespace
    plan = caravan_plan()
    current = ColonyPlan()
    current.control['resource_policy'] = {'Pemmican': {'reserve': 50}}
    game = SimpleNamespace(invoke=AsyncMock(return_value={'accepted': True,
        'carriedCargo': [],
        'costList': [{'defName': 'Pemmican', 'count': 60}],
        'materials': {'rows': [{'defName': 'Pemmican', 'available': 100}]}}))
    with pytest.raises(ValueError, match='reservation'):
        await validate_allocations(plan.spec, current, game)


@pytest.mark.asyncio
async def test_revision_cannot_drop_a_cancelled_live_cargo_hold():
    from rimbot.colony_plan import PlanSpec
    from rimbot.resource_accounting import validate_allocations
    plan = caravan_plan()
    plan.cancel('trip')
    with pytest.raises(ValueError, match='Retain the caravan action'):
        await validate_allocations(PlanSpec(), plan, None)


def test_player_route_change_invalidates_arrival_expectation():
    value = observation()
    value['caravans'][0]['destination'] = 30
    assert outcome(value) == 'invalidated'


@pytest.mark.asyncio
async def test_preexisting_crew_food_does_not_count_as_newly_loaded_cargo():
    from types import SimpleNamespace
    from unittest.mock import Mock
    from rimbot.world_progression import reconcile_world
    plan = caravan_plan()
    plan.spec.steps[0].action.caravan_target.carried_cargo = {'Pemmican': 20}
    value = observation()
    pawn = value['caravans'][0]['pawns'][0]
    pawn.update(thingId='Thing_pawn1', inventory=[{'defName': 'Pemmican', 'count': 60}])
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=AsyncMock(return_value=value)), signal=Mock())
    await reconcile_world(rt)
    assert plan.progress['trip'].state == 'waiting'
    assert not plan.progress['trip'].issued['0'].get('cargo_departed')
    pawn['inventory'][0]['count'] = 80
    await reconcile_world(rt)
    assert plan.progress['trip'].state == 'complete'

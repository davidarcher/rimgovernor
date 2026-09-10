from types import SimpleNamespace
import asyncio
from unittest.mock import AsyncMock

import pytest
from mcp.types import CallToolResult

from rimgovernor.bridge import BridgeClient, BridgeError
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.colony_plan import ColonyPlan, PlanStep, StepProgress
from rimgovernor.hands import Hands
from rimgovernor.projects import ProjectBook


FAULT = (
    "Failed to claim runtime ownership for 'rimgovernor-trial': failed to publish runtime state: "
    "rename C:\\GABS\\.runtime-2910154312.tmp C:\\GABS\\runtime.json: Access is denied."
)
CLAIM_FAULT = ("Failed to claim runtime ownership for 'rimgovernor-trial': a launch claim for 'rimgovernor-trial' "
               "was published while preparing this operation; re-check games_status and retry")


def result(payload, error=False):
    return CallToolResult(content=[], structuredContent=payload, isError=error)


@pytest.mark.asyncio
async def test_native_connection_allows_slow_start_without_takeover_or_replay():
    session=SimpleNamespace(call_tool=AsyncMock(return_value=result({'connected':True})))
    await BridgeClient(session).connect()
    session.call_tool.assert_any_await('games_connect',{'gameId':'rimgovernor-trial','timeout':60})
    assert sum(c.args[0]=='games_connect' for c in session.call_tool.await_args_list)==1
    session.call_tool.assert_any_await('games_tool_names',{'gameId':'rimgovernor-trial','cursor':'','query':'rimworld/load_game_ready'})


def game_with(responses, tool, properties):
    session = SimpleNamespace(call_tool=AsyncMock(side_effect=responses))
    game = BridgeGame(BridgeClient(session))
    game.schemas[tool] = {'type': 'object', 'properties': properties, 'additionalProperties': False}
    return game, session


@pytest.fixture(autouse=True)
def no_retry_delay(monkeypatch):
    sleep = AsyncMock()
    monkeypatch.setattr('rimgovernor.bridge.asyncio.sleep', sleep)
    return sleep


@pytest.mark.asyncio
async def test_observation_retries_publish_fault_with_bounded_backoff(no_retry_delay):
    fault = result({'message': FAULT}, True)
    game, session = game_with([fault, fault, result({'zones': []})], 'home/list_zones', {})
    assert await game.query('home/list_zones') == {'zones': []}
    assert session.call_tool.await_count == 3
    assert [call.args[0] for call in no_retry_delay.await_args_list] == [0.05, 0.1]


@pytest.mark.asyncio
@pytest.mark.parametrize('op',['status','events','start','heartbeat','pause'])
async def test_native_claim_race_retries_only_clock_reads(op):
    from rimgovernor.clock_control import PlayClock
    session=SimpleNamespace(call_tool=AsyncMock(side_effect=[
        result({'message':CLAIM_FAULT},True),result({'running':True}),result({'success':True})]))
    clock=PlayClock(BridgeClient(session))
    if op in ('status','events'):
        assert await clock.call(op=op)=={'success':True}
        assert session.call_tool.await_count==3
    else:
        with pytest.raises(BridgeError):await clock.call(op=op)
        assert session.call_tool.await_count==1


@pytest.mark.asyncio
async def test_claim_race_observation_stops_after_three_attempts():
    fault=result({'message':CLAIM_FAULT},True)
    game,session=game_with([fault,result({}),fault,result({}),fault],'home/list_zones',{})
    with pytest.raises(BridgeError):await game.query('home/list_zones')
    assert session.call_tool.await_count==5


@pytest.mark.asyncio
async def test_one_client_cannot_race_its_own_native_ownership_claim():
    entered=asyncio.Event();release=asyncio.Event();calls=[]
    async def call(name,arguments):
        calls.append(name)
        if name=='first':
            entered.set()
            await release.wait()
        return result({'success':True})
    client=BridgeClient(SimpleNamespace(call_tool=call))
    first=asyncio.create_task(client.core('first'))
    await entered.wait()
    second_started=asyncio.Event()
    async def second_call():
        second_started.set()
        return await client.core('second')
    second=asyncio.create_task(second_call())
    await second_started.wait()
    assert calls==['first'] and not second.done()
    release.set()
    await asyncio.gather(first,second)
    assert calls==['first','second']


@pytest.mark.asyncio
@pytest.mark.parametrize('message', ['Native refusal', 'runtime.json: Access is denied.',
    FAULT.replace('Access is denied.', 'Disk full.')])
async def test_other_errors_are_not_retried(message):
    game, session = game_with([result({'message': message}, True)], 'home/list_zones', {})
    with pytest.raises(BridgeError):
        await game.query('home/list_zones')
    assert session.call_tool.await_count == 1


@pytest.mark.asyncio
async def test_mutation_receipt_loss_is_never_replayed():
    game, session = game_with([result({'message': FAULT}, True)], 'home/place_building',
        {'dryRun': {'type': 'boolean'}})
    with pytest.raises(BridgeError):
        await game.invoke('home/place_building', {'dryRun': False}, allow_write=True)
    assert session.call_tool.await_count == 1


@pytest.mark.asyncio
async def test_installation_preview_can_recover():
    game, session = game_with([result({'message': FAULT}, True), result({'accepted': True})],
        'home/install', {'dryRun': {'type': 'boolean'}})
    assert await game.invoke('home/install', {'dryRun': True}) == {'accepted': True}
    assert session.call_tool.await_count == 2


@pytest.mark.asyncio
@pytest.mark.parametrize('tool', ['home/research', 'home/order', 'home/trade'])
async def test_mixed_operation_tools_without_explicit_preview_are_not_retried(tool):
    game, session = game_with([result({'message': FAULT}, True)], tool, {})
    with pytest.raises(BridgeError):
        await game.invoke(tool, {})
    assert session.call_tool.await_count == 1


@pytest.mark.asyncio
async def test_repeated_faults_preserve_receipts_across_restore_without_orders():
    fault = result({'message': FAULT}, True)
    built = {'buildings': [{'thingId': 'Wall1', 'defName': 'Wall',
        'position': {'x': 1, 'z': 1}, 'status': 'built'}]}
    game, session = game_with([fault] * 12 + [result(built)], 'home/list_buildings',
        {key: {} for key in ('match', 'x', 'z', 'radius', 'aggregate', 'playerOnly')})
    book = ProjectBook()
    row = book.upsert({'title': 'Wall', 'targets': [
        {'kind': 'building', 'def_name': 'Wall', 'x': 1, 'z': 1}]})
    step = PlanStep(id='wall', title='Wall', completion_criteria='Built',
        action={'kind': 'place_buildings', 'placements': [{'def_name': 'Wall', 'x': 1, 'z': 1}]})
    receipts = {'0': {'confirmed': True, 'outcome': 'placed'}}
    rt = SimpleNamespace(current_plan=ColonyPlan(spec={'steps': [step]}, progress={
        'wall': StepProgress(state='waiting', project_id=row.id, issued=receipts)}),
        projects=book, game=game, signal=lambda *a: None, context_token='load', chat_revision=0)
    for _ in range(4):
        await rt.projects.reconcile(game)
        BridgeRuntime.reconcile_plan(rt)
        progress = rt.current_plan.progress['wall']
        assert progress.state == 'waiting'
        assert progress.failure.code == 'observation_unavailable'
        assert progress.issued == receipts
        await Hands().advance(rt)
        rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
        rt.projects = ProjectBook(rt.projects.dump())
    await rt.projects.reconcile(game)
    BridgeRuntime.reconcile_plan(rt)
    progress = rt.current_plan.progress['wall']
    assert progress.state == 'complete' and progress.failure is None
    assert progress.issued == receipts
    assert rt.projects.rows[0].matched_ids == ['Wall1']
    assert session.call_tool.await_count == 13
    assert all(call.args[1]['tool'] == 'home/list_buildings' for call in session.call_tool.await_args_list)


async def test_launch_claim_read_refreshes_status_before_bounded_retry():
    game, session = game_with([result({'message': CLAIM_FAULT}, True), result({'running': True}),
        result({'zones': []})], 'home/list_zones', {})
    assert await game.query('home/list_zones') == {'zones': []}
    assert [c.args[0] for c in session.call_tool.await_args_list] == ['games_call_tool', 'games_status', 'games_call_tool']
    assert session.call_tool.await_args_list[1].args[1] == {'gameId': 'rimgovernor-trial'}


async def test_launch_claim_mutation_failure_never_refreshes_or_replays():
    game, session = game_with([result({'message': CLAIM_FAULT}, True)], 'home/place_building', {'dryRun': {'type': 'boolean'}})
    with pytest.raises(BridgeError): await game.invoke('home/place_building', {'dryRun': False}, allow_write=True)
    assert session.call_tool.await_count == 1


async def test_repeated_launch_claim_read_failure_exhausts_two_retries():
    fault = result({'message': CLAIM_FAULT}, True)
    game, session = game_with([fault, result({}), fault, result({}), fault], 'home/list_zones', {})
    with pytest.raises(BridgeError): await game.query('home/list_zones')
    assert session.call_tool.await_count == 5


@pytest.mark.parametrize('op', ['status','events','start','pause','heartbeat'])
async def test_clock_launch_claim_recovery_is_read_only(op):
    from rimgovernor.clock_control import PlayClock
    session=SimpleNamespace(call_tool=AsyncMock(side_effect=[
        result({'message':CLAIM_FAULT},True),result({'running':True}),result({'success':True})]))
    clock=PlayClock(BridgeClient(session))
    if op in ('status','events'):
        assert await clock.call(op=op)=={'success':True}
        assert [c.args[0] for c in session.call_tool.await_args_list]==['games_call_tool','games_status','games_call_tool']
    else:
        with pytest.raises(BridgeError):await clock.call(op=op)
        assert session.call_tool.await_count==1


@pytest.mark.asyncio
async def test_camera_read_recovers_claim_race_but_pan_does_not():
    from rimgovernor.dashboard_controls import camera_call
    session = SimpleNamespace(call_tool=AsyncMock(side_effect=[result({'message':CLAIM_FAULT},True),
        result({'running':True}), result({'success':True,'mapId':'Map_0'})]))
    rt = SimpleNamespace(bridge=BridgeClient(session))
    assert (await camera_call(rt,'rimworld/get_camera_state',{}))['mapId']=='Map_0'
    assert session.call_tool.await_count==3
    session.call_tool=AsyncMock(side_effect=[result({'message':CLAIM_FAULT},True)])
    with pytest.raises(ValueError, match='Native player request failed'):
        await camera_call(rt,'rimworld/move_camera',{'direction':'left'})
    assert session.call_tool.await_count==1

@pytest.mark.parametrize('op', ['status', 'events', 'start', 'pause', 'heartbeat'])
async def test_temporary_gabp_loss_only_retries_clock_observations(op):
    from rimgovernor.clock_control import PlayClock
    fault = result({'message': "Game 'rimgovernor-trial' is not connected via GABP."}, True)
    session = SimpleNamespace(call_tool=AsyncMock(side_effect=[fault, result({'running': True}), result({'success': True})]))
    clock = PlayClock(BridgeClient(session))
    if op in ('status', 'events'):
        assert await clock.call(op=op) == {'success': True}
        assert [c.args[0] for c in session.call_tool.await_args_list] == ['games_call_tool', 'games_status', 'games_call_tool']
    else:
        with pytest.raises(BridgeError):
            await clock.call(op=op)
        assert session.call_tool.await_count == 1


async def test_persistent_gabp_loss_never_reconnects_or_restarts_the_game():
    fault = result({'message': "Game 'rimgovernor-trial' is not connected via GABP."}, True)
    game, session = game_with([fault, result({}), fault, result({}), fault], 'home/list_zones', {})
    with pytest.raises(BridgeError):
        await game.query('home/list_zones')
    assert [c.args[0] for c in session.call_tool.await_args_list] == ['games_call_tool', 'games_status', 'games_call_tool', 'games_status', 'games_call_tool']

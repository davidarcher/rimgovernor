from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from mcp.types import CallToolResult

from rimbot.bridge import BridgeClient, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyPlan, PlanStep, StepProgress
from rimbot.hands import Hands
from rimbot.projects import ProjectBook


FAULT = (
    "Failed to claim runtime ownership for 'rimbot-trial': failed to publish runtime state: "
    "rename C:\\GABS\\.runtime-2910154312.tmp C:\\GABS\\runtime.json: Access is denied."
)
CLAIM_FAULT = ("Failed to claim runtime ownership for 'rimbot-trial': a launch claim for 'rimbot-trial' "
               "was published while preparing this operation; re-check games_status and retry")


def result(payload, error=False):
    return CallToolResult(content=[], structuredContent=payload, isError=error)


def game_with(responses, tool, properties):
    session = SimpleNamespace(call_tool=AsyncMock(side_effect=responses))
    game = BridgeGame(BridgeClient(session))
    game.schemas[tool] = {'type': 'object', 'properties': properties, 'additionalProperties': False}
    return game, session


@pytest.fixture(autouse=True)
def no_retry_delay(monkeypatch):
    sleep = AsyncMock()
    monkeypatch.setattr('rimbot.bridge.asyncio.sleep', sleep)
    return sleep


@pytest.mark.asyncio
async def test_observation_retries_publish_fault_with_bounded_backoff(no_retry_delay):
    fault = result({'message': FAULT}, True)
    game, session = game_with([fault, fault, result({'zones': []})], 'home/list_zones', {})
    assert await game.query('home/list_zones') == {'zones': []}
    assert session.call_tool.await_count == 3
    assert [call.args[0] for call in no_retry_delay.await_args_list] == [0.05, 0.1]


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
    assert session.call_tool.await_args_list[1].args[1] == {'gameId': 'rimbot-trial'}


async def test_launch_claim_mutation_failure_never_refreshes_or_replays():
    game, session = game_with([result({'message': CLAIM_FAULT}, True)], 'home/place_building', {'dryRun': {'type': 'boolean'}})
    with pytest.raises(BridgeError): await game.invoke('home/place_building', {'dryRun': False}, allow_write=True)
    assert session.call_tool.await_count == 1


async def test_repeated_launch_claim_read_failure_exhausts_two_retries():
    fault = result({'message': CLAIM_FAULT}, True)
    game, session = game_with([fault, result({}), fault, result({}), fault], 'home/list_zones', {})
    with pytest.raises(BridgeError): await game.query('home/list_zones')
    assert session.call_tool.await_count == 5

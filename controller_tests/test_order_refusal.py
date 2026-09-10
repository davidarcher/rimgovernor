from unittest.mock import AsyncMock

import pytest
from mcp.types import CallToolResult
from rimbot.bridge import BridgeError
from rimbot.order_refusal import refused_preview
from rimbot.order_refusal import prerequisites
from test_colony_controller import Replay


def refusal(**changes):
    payload = dict(success=False, applied=False, dryRun=True, errorKind='job_refused', error='Target is on fire')
    payload.update(changes)
    return BridgeError('home/order', CallToolResult(content=[], structuredContent=payload, isError=True))


def test_rot_and_temperature_drift_do_not_reopen_refused_preview():
    rt = Replay()
    state = {'known': True, 'targets': [{'id': 'stock', 'rotTicks': 100, 'temperature': 22, 'burning': True}]}
    control = {'upkeep': {'SecureSupplies': state}}
    before = prerequisites(rt.facts, rt.people, control)
    state['targets'][0].update(rotTicks=90, temperature=23)
    assert prerequisites(rt.facts, rt.people, control) == before
    state['targets'][0]['burning'] = False
    assert prerequisites(rt.facts, rt.people, control) != before


@pytest.mark.parametrize('change', [dict(dryRun=False), dict(applied=True), dict(applied=None),
    dict(errorKind='job_unverified'), dict(errorKind='unexpected'), dict(success=None)])
def test_only_confirmed_refused_previews_are_reconsidered(change):
    assert refused_preview(refusal(**change)) is None


@pytest.mark.asyncio
async def test_refused_order_preview_keeps_reviews_alive_and_waits_for_new_evidence():
    rt = Replay()
    rt.controller.skills.compile = AsyncMock(side_effect=refusal())
    await rt.controller.cycle()
    goals = [g for g in rt.current_plan.colony_goals.values() if g.evidence.get('order_preview_refusal')]
    assert goals and all(g.status == 'blocked' for g in goals)
    count = rt.controller.skills.compile.await_count
    await rt.controller.cycle()
    assert rt.controller.skills.compile.await_count == count
    rt.people[0]['job'] = 'HaulToCell'
    await rt.controller.cycle()
    assert rt.controller.skills.compile.await_count > count
    assert rt.mode == 'automate' and not rt.current_plan.spec.steps


@pytest.mark.asyncio
async def test_unknown_or_dispatch_error_is_not_swallowed_by_compilation():
    rt = Replay()
    rt.controller.skills.compile = AsyncMock(side_effect=refusal(dryRun=False))
    with pytest.raises(BridgeError):
        await rt.controller.cycle()

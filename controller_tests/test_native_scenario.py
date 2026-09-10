import asyncio
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import pytest

from rimbot.native_scenario import advance_game, ScenarioInterrupted


def scenario():
    status = dict(skipped=[], blocks=dict(colonists=True, threats=True),
                  letters=[dict(id='Letter1', label='Ancient danger', letterDef='ThreatBig')],
                  ui=dict(modalOpen=False), time=dict(paused=True),
                  counts=dict(hostileCount=0, huntingPredatorCount=0, downedCount=0),
                  colonists=[dict(dead=False, downed=False, bleeding=False)])
    state = dict(owner='test', epoch=1, active=False, pauseVerified=True,
                 lastTick=140, startTick=100, stopReason='letter_pause', newestCursor=2)
    calls = []

    async def change(speed, *, max_ticks):
        calls.append(max_ticks)
        if len(calls) > 1:
            state.update(epoch=2, startTick=140, lastTick=140+max_ticks,
                         stopReason='tick_budget', newestCursor=3)
        return dict(state, active=True, newestCursor=1)

    async def call(**kw):
        if kw['op'] == 'status':
            return deepcopy(state)
        events = [] if kw['afterCursor'] == 2 else [dict(epoch=1, kind='letter_pause',
                    event=dict(letterId='Letter1', source='LetterStack.ReceiveLetter'))]
        return dict(events=events, nextCursor=2, gap=False)

    async def query(name, **kw):
        return status if name == 'home/status' else dict(colonyId='c', mapId=1, loadToken='l', tick=state['lastTick'])

    supervisor = SimpleNamespace(owner='test', change=AsyncMock(side_effect=change),
        call=AsyncMock(side_effect=call), poll=AsyncMock(return_value=[]), hold=None)
    rt = SimpleNamespace(review_task=None, lock=asyncio.Lock(), game=SimpleNamespace(query=AsyncMock(side_effect=query)),
                         supervisor=supervisor, note=Mock(), clock_events=[], receive_clock_events=Mock())
    return rt, status, state, calls


async def test_default_warning_resumes_only_remaining_ticks_and_records_evidence():
    rt, _, _, calls = scenario()
    report = {}
    result = await advance_game(rt, 100, report)
    assert calls == [100, 60]
    assert result['lastTick'] == 200
    assert report['simulation'][0]['completed']
    assert report['simulation'][0]['interruptions'][0]['acknowledgedLetterId'] == 'Letter1'
    assert rt.receive_clock_events.call_count == 2


@pytest.mark.parametrize('mutation', [
    lambda s: s.update(skipped=[{'field': 'threats'}]),
    lambda s: s['blocks'].update(threats=False),
    lambda s: s['ui'].update(modalOpen=True),
    lambda s: s['counts'].update(hostileCount=1),
    lambda s: s['counts'].pop('huntingPredatorCount'),
    lambda s: s['colonists'][0].update(bleeding=True),
    lambda s: s['letters'][0].update(id='Unrelated'),
    lambda s: s['letters'][0].update(label='Raid'),
])
async def test_unsafe_unknown_or_unattributed_warning_never_resumes(mutation):
    rt, status, _, calls = scenario()
    mutation(status)
    report = {}
    with pytest.raises(ScenarioInterrupted):
        await advance_game(rt, 100, report)
    assert calls == [100]
    assert report['simulation'][0]['failure']


@pytest.mark.parametrize('reason', ['external_pause', 'force_paused', 'hostile', 'requested_pause', 'session_changed'])
async def test_other_interruptions_are_not_acknowledged(reason):
    rt, _, state, calls = scenario()
    state['stopReason'] = reason
    with pytest.raises(ScenarioInterrupted):
        await advance_game(rt, 100, {})
    assert calls == [100]


async def test_interruption_scenario_can_opt_out():
    rt, _, _, calls = scenario()
    with pytest.raises(ScenarioInterrupted, match='not approved'):
        await advance_game(rt, 100, {}, expected_letters=())
    assert calls == [100]


async def test_changed_identity_blocks_resume():
    rt, _, _, calls = scenario()
    original = rt.game.query.side_effect
    reads = 0

    async def query(name, **kwargs):
        nonlocal reads
        result = await original(name, **kwargs)
        if name == 'home/colony_identity':
            reads += 1
            if reads == 3:
                result['loadToken'] = 'new'
        return result

    rt.game.query.side_effect = query
    with pytest.raises(ScenarioInterrupted, match='identity changed'):
        await advance_game(rt, 100, {})
    assert calls == [100]


async def test_exact_budget_requires_no_warning_handling():
    rt, _, state, calls = scenario()
    state.update(stopReason='tick_budget', lastTick=200)
    assert (await advance_game(rt, 100, {}))['lastTick'] == 200
    assert calls == [100]
    rt.note.assert_not_called()
    rt.supervisor.poll.assert_awaited_once()
    rt.receive_clock_events.assert_called_once()


async def test_budget_event_reaches_runtime_before_next_decision_revision():
    rt, _, state, _ = scenario()
    state.update(stopReason='tick_budget', lastTick=200)
    rt.chat_revision = 1
    event = dict(kind='tick_budget')
    rt.supervisor.poll.return_value = [event]
    def receive():
        assert rt.clock_events == [event]
        rt.chat_revision += 1
        rt.clock_events.clear()
    rt.receive_clock_events.side_effect = receive
    await advance_game(rt, 100, {})
    assert rt.chat_revision == 2 and rt.clock_events == []


@pytest.mark.parametrize('fault', ['gap', 'missing', 'wrong_source'])
async def test_event_evidence_must_be_complete(fault):
    rt, _, _, calls = scenario()
    original = rt.supervisor.call.side_effect

    async def call(**kw):
        result = await original(**kw)
        if kw['op'] == 'events':
            if fault == 'gap': result['gap'] = True
            if fault == 'missing': result['events'] = []
            if fault == 'wrong_source' and result['events']:
                result['events'][0]['event']['source'] = 'unknown'
        return result

    rt.supervisor.call.side_effect = call
    with pytest.raises(ScenarioInterrupted):
        await advance_game(rt, 100, {})
    assert calls == [100]


async def test_timeout_pauses_only_owned_clock_and_retains_failure():
    rt, _, state, _ = scenario()
    state['active'] = True
    report = {}
    with pytest.raises(TimeoutError):
        await advance_game(rt, 100, report, timeout=.01)
    assert any(c.kwargs.get('op') == 'pause' for c in rt.supervisor.call.call_args_list)
    assert report['simulation'][0]['failure']


async def test_foreign_owner_is_never_paused_by_cleanup():
    rt, _, state, _ = scenario()
    state['owner'] = 'another-test'
    with pytest.raises(ScenarioInterrupted, match='ownership'):
        await advance_game(rt, 100, {})
    assert not any(c.kwargs.get('op') == 'pause' for c in rt.supervisor.call.call_args_list)


async def test_combat_wait_requires_exact_committed_targets():
    rt, _, _, _ = scenario()
    rt.current_plan = SimpleNamespace(control={'combat': {'targets': ['enemy']}})
    with pytest.raises(ValueError, match='exact committed'):
        await advance_game(rt, 100, {}, combat_targets=['unrelated'])
    rt.supervisor.change.assert_not_called()


async def test_combat_wait_retains_unexpected_injury_stop():
    rt, _, state, _ = scenario()
    rt.current_plan = SimpleNamespace(control={'combat': {'targets': ['enemy']}})
    state.update(stopReason='injury')
    rt.supervisor.change.side_effect = None
    rt.supervisor.change.return_value = dict(state, active=True, newestCursor=1)
    with pytest.raises(ScenarioInterrupted, match='Unexpected native interruption'):
        await advance_game(rt, 100, {}, expected_letters=(), combat_targets=['enemy'])
    rt.supervisor.change.assert_awaited_once_with('Superfast', max_ticks=100, mode='combat', ignored_hostiles='enemy')

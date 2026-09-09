import pytest
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.memory import update_memory
from rimbot.strategic_state import StrategicState


def call(notes, op, id='camp', **kwargs):
    return update_memory(notes, op, id, tick=100, load_token='load1', **kwargs)


def test_persistence_update_and_previous_load_warning():
    state = StrategicState()
    call(state.memories, 'write', text='Gravel near camp', evidence='Inspected terrain; check crop eligibility')
    restored = StrategicState(state.dump())
    assert call(restored.memories, 'read')['note']['text'] == 'Gravel near camp'
    result = update_memory(restored.memories, 'read', 'camp', tick=50, load_token='older-save')
    assert result['from_previous_load'] and result['advisory']
    call(restored.memories, 'write', text='Updated observation', evidence='Fresh query')
    assert len(restored.memories) == 1
    assert call(restored.memories, 'delete')['deleted']
    assert not call(restored.memories, 'delete')['deleted']
    assert not StrategicState().memories


def test_budget_does_not_evict_existing_lessons():
    notes = {}
    for n in range(20):
        call(notes, 'write', f'n{n}', text='Lesson', evidence='Observation')
    with pytest.raises(ValueError):
        call(notes, 'write', 'overflow', text='Lesson', evidence='Observation')
    call(notes, 'write', 'n0', text='Revision', evidence='New observation')
    assert len(notes) == 20 and notes['n0']['text'] == 'Revision'


@pytest.mark.parametrize('kwargs', [dict(text=''), dict(text='x'*1001), dict(evidence=''), dict(evidence='x'*501)])
def test_invalid_note_does_not_mutate(kwargs):
    notes = {}
    args = dict(text='Lesson', evidence='Observation') | kwargs
    with pytest.raises(ValueError):
        call(notes, 'write', **args)
    assert notes == {}


def test_reads_are_copies_and_unknown_reads_fail():
    notes = {}
    with pytest.raises(ValueError):
        call(notes, 'read')
    call(notes, 'write', text='Lesson', evidence='Observation')
    call(notes, 'read')['note']['text'] = 'changed'
    assert notes['camp']['text'] == 'Lesson'


@pytest.mark.asyncio
@pytest.mark.parametrize('token,revision', [('old', 2), ('current', 1)])
async def test_runtime_rejects_stale_colony_or_direction(token, revision):
    rt = SimpleNamespace(lock=asyncio.Lock(), sync_identity=AsyncMock(),
        context_token='current', chat_revision=2, strategic_state=StrategicState(),
        persist=Mock(), note=Mock())
    with pytest.raises(ValueError, match='discarded'):
        await BridgeRuntime.memory(rt, 'write', 'camp', 'Lesson', 'Observation',
            expected_token=token, expected_revision=revision)
    assert not rt.strategic_state.memories
    rt.persist.assert_not_called()


@pytest.mark.asyncio
async def test_player_forget_checks_version_and_invalidates_review():
    from rimbot.strategic_state import fingerprint
    state = StrategicState()
    call(state.memories, 'write', text='Lesson', evidence='Observation')
    rt = SimpleNamespace(lock=asyncio.Lock(), sync_identity=AsyncMock(),
        context_token='current', chat_revision=2, strategic_state=state,current_plan=SimpleNamespace(control={}),
        persist=Mock(), note=Mock(), mode='automate', wake=asyncio.Event())
    version = fingerprint(state.memories['camp'])
    for session, stamp in [('old', version), ('current', 'stale')]:
        with pytest.raises(ValueError):
            await BridgeRuntime.forget_memory(rt, 'camp', session, stamp)
        assert 'camp' in state.memories and rt.chat_revision == 2
    await BridgeRuntime.forget_memory(rt, 'camp', 'current', version)
    assert not state.memories and rt.chat_revision == 3 and rt.wake.is_set()
    assert state.pending[0]['kind'] == 'player.forgot_memory'

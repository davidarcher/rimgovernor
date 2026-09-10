import importlib.util
import asyncio
import json
from pathlib import Path
import sqlite3

import pytest
from mcp.types import CallToolResult
from rimbot.bridge import BridgeClient, BridgeError
from rimbot.flight_recorder import FlightRecorder, read_timeline, recording_action


def test_retention_and_truncation_are_explicit(tmp_path):
    path = tmp_path/'timeline.jsonl'
    record = FlightRecorder(path, segment_bytes=1024, segments=2, payload_bytes=128)
    for index in range(20):
        record.event('sample', value='x'*500, index=index)
    rows = list(read_timeline(path))
    assert rows[0]['kind']=='recording_gap'
    assert any(r.get('payload', {}).get('truncated') for r in rows)
    assert len(list(tmp_path.iterdir()))==2
    assert record.stats()['truncated']==21


def test_truncated_receipt_keeps_request_correlation(tmp_path):
    record=FlightRecorder(tmp_path/'timeline.jsonl',payload_bytes=128)
    request=record.event('native_request',tool='games_call_tool',arguments={})
    record.event('native_response',request=request,result={'value':'x'*2000})
    row=list(read_timeline(record.path))[-1]
    assert row['payload']['truncated'] and row['payload']['request']==request


@pytest.mark.asyncio
async def test_transport_records_before_write_and_preserves_error(tmp_path, monkeypatch):
    path = tmp_path/'timeline.jsonl'
    monkeypatch.setenv('RIMBOT_FLIGHT_RECORDER', str(path))
    class Session:
        async def call_tool(self, name, arguments):
            rows = list(read_timeline(path))
            assert rows[-1]['kind']=='native_request'
            assert rows[-1]['payload']['arguments']==arguments
            return CallToolResult(content=[], isError=True, structuredContent={'error':'native rejected'})
    with pytest.raises(BridgeError, match='native rejected'):
        await BridgeClient(Session()).call('home/place_building', dryRun=False)
    assert [r['kind'] for r in read_timeline(path)] == ['coverage', 'native_request', 'native_response', 'native_error']


@pytest.mark.asyncio
async def test_transport_records_lost_response_without_retry(tmp_path, monkeypatch):
    path = tmp_path/'timeline.jsonl'
    monkeypatch.setenv('RIMBOT_FLIGHT_RECORDER', str(path))
    class Session:
        calls = 0
        async def call_tool(self, *args):
            self.calls += 1
            raise TimeoutError('lost response')
    session = Session()
    with pytest.raises(TimeoutError):
        await BridgeClient(session).call('write')
    assert session.calls==1
    assert list(read_timeline(path))[-1]['kind']=='native_error'


def test_buffered_receipt_is_checkpointed_by_next_request(tmp_path, monkeypatch):
    path=tmp_path/'timeline.jsonl'
    sync=[]
    monkeypatch.setattr('rimbot.flight_recorder.os.fsync', lambda _:sync.append(path.read_bytes()))
    record=FlightRecorder(path)
    request=record.event('native_request', tool='write')
    record.event('native_response', request=request, result={'accepted':True}, durable=False)
    assert len(sync)==2
    record.event('native_request', tool='observe')
    assert b'"accepted":true' in sync[-1]
    assert record.stats()['durable_records']==3


def test_corrupt_tail_is_reported(tmp_path):
    path = tmp_path/'timeline.jsonl'
    FlightRecorder(path)
    with path.open('a') as stream:
        stream.write('{broken')
    assert list(read_timeline(path))[-1]['kind']=='recording_gap'


def test_payload_encoding_preserves_unicode_nested_values_and_single_conversion(tmp_path):
    class Value:
        calls = 0
        def __str__(self):
            self.calls += 1
            return 'quoted "colonist" — 雪'
    value = Value()
    path = tmp_path/'timeline.jsonl'
    record = FlightRecorder(path)
    record.event('sample', nested={'value': value, 'items': [None, True, '\\']})
    assert value.calls == 1
    row = list(read_timeline(path))[-1]
    assert row['payload']['nested'] == {'value': 'quoted "colonist" — 雪', 'items': [None, True, '\\']}
    assert all(seconds >= 0 for seconds in record.stats()['phase_seconds'].values())


def test_recorder_reopens_after_close_and_preserves_rotation_and_durability(tmp_path, monkeypatch):
    path = tmp_path/'timeline.jsonl'
    synced = []
    monkeypatch.setattr('rimbot.flight_recorder.os.fsync', lambda fd: synced.append(fd))
    record = FlightRecorder(path, segment_bytes=1024, segments=3, payload_bytes=2048)
    record.event('native_request', value='a'*1100)
    record.event('native_response', value='b', durable=False)
    before = len(synced)
    record.close()
    assert len(synced) == before+1
    record.event('native_request', value='c')
    record.close()
    rows = list(read_timeline(path))
    assert [row['kind'] for row in rows] == ['coverage','native_request','native_response','native_request']
    assert [row['sequence'] for row in rows] == [1,2,3,4]
    assert rows[1]['payload']['value'] == 'a'*1100


def test_invalid_record_and_unrelated_sidecar_are_tolerated(tmp_path):
    path = tmp_path/'timeline.jsonl'
    FlightRecorder(path)
    path.with_suffix('.jsonl.notes').write_text('operator notes')
    with path.open('a') as stream:
        stream.write('{}\n')
    rows = list(read_timeline(path))
    assert rows[0]['kind']=='coverage'
    assert rows[-1]['kind']=='recording_gap'


def test_partial_utf8_tail_preserves_earlier_records(tmp_path):
    path = tmp_path/'timeline.jsonl'
    FlightRecorder(path)
    with path.open('ab') as stream:
        stream.write(b'{"partial":"\xe2')
    rows = list(read_timeline(path))
    assert rows[0]['kind']=='coverage'
    assert rows[-1]['kind']=='recording_gap'


@pytest.mark.asyncio
async def test_interleaved_calls_keep_their_dispatch_context(tmp_path, monkeypatch):
    path = tmp_path/'timeline.jsonl'
    monkeypatch.setenv('RIMBOT_FLIGHT_RECORDER', str(path))
    started, release = asyncio.Event(), asyncio.Event()
    class Session:
        async def call_tool(self, name, arguments):
            if name == 'slow':
                started.set()
                await release.wait()
            return CallToolResult(content=[])
    bridge = BridgeClient(Session())
    other = BridgeClient(Session())
    direction = {'revision':1}
    bridge.recording_context = lambda: direction
    other.recording_context = lambda: direction
    async def dispatch(client, tool):
        with recording_action(tool+'-action', tool+'-goal'):
            return await client.core(tool)
    task = asyncio.create_task(dispatch(bridge,'slow'))
    await started.wait()
    direction['revision'] = 2
    await dispatch(other,'fast')
    release.set()
    await task
    responses = [r for r in read_timeline(path) if r['kind']=='native_response']
    assert [(r['payload']['tool'], r['context']['revision']) for r in responses] == [('fast',2), ('slow',1)]
    assert [(r['context']['action_id'],r['context']['goal_id']) for r in responses] == [
        ('fast-action','fast-goal'),('slow-action','slow-goal')]
    await other.core('unscoped')
    assert 'action_id' not in list(read_timeline(path))[-1]['context']

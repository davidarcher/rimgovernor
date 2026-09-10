from types import SimpleNamespace
from unittest.mock import Mock
import time
from rimgovernor.tool_diagnostics import record
from rimgovernor.store import Store


def test_rejection_keeps_identity_and_bounds_large_payload():
    rt=SimpleNamespace(note=Mock())
    record(rt,{'id':'call1','function':{'name':'wiki_lookup'}},{'query':'roof support'},
        {'error':'x'*10000},time.monotonic(),'rejected')
    args,fields=rt.note.call_args
    assert 'roof support' in args[1] and 'rejected' in args[1]
    assert fields['call_id']=='call1'
    assert fields['result']['truncated'] and len(fields['result']['preview'])==6000


def test_diagnostic_attempts_are_opt_in(tmp_path):
    store=Store(tmp_path/'state.sqlite')
    try:
        store.event('colony','planner_tool',text='inspect')
        store.event('colony','summary',text='Plan ready')
        assert len(store.history('colony'))==1
        assert len(store.history('colony',include_diagnostics=True))==2
    finally:store.close()

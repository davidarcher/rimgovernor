import json
import pytest
from rimbot.review_evidence import ReviewEvidence
from rimbot.request_budget import fit_request


def test_exact_result_is_isolated_and_searchable():
    store=ReviewEvidence()
    result={'cells':[{'x':2,'z':8,'roof':None}],'unknown':None}
    identity=store.add('native_home__get_cells_plus',{'x':2,'z':8},result)
    result['cells'].clear()
    read=store.read(identity)
    assert read['result']['cells']==[{'x':2,'z':8,'roof':None}]
    assert read['historical'] and read['captured_at']>0
    read['result']['cells'].clear()
    assert len(store.read(identity)['result']['cells'])==1
    assert store.index('get_cells')['items'][0]['id']==identity
    assert not store.index('unrelated')['items']


def test_explicit_eviction_and_review_isolation():
    store=ReviewEvidence(max_bytes=500)
    first=store.add('read',{}, {'text':'a'*250})
    second=store.add('read',{}, {'text':'b'*250})
    assert store.index()['evicted']==1
    with pytest.raises(ValueError,match='evicted'):store.read(first)
    assert store.read(second)['result']['text']=='b'*250
    with pytest.raises(ValueError):ReviewEvidence().read(second)
    assert store.add('huge',{}, {'text':'x'*1000}) is None


def test_compaction_removes_old_message_but_keeps_searchable_index():
    store=ReviewEvidence()
    original={'facts':'x'*24000}
    identity=store.add('native_tool',{'query':'walls'},original)
    messages=[{'role':'system','content':'Follow evidence'}, {'role':'user','content':'colony'},
        {'role':'user','content':json.dumps({'review_evidence':store.index()})},
        {'role':'assistant','tool_calls':[{'id':'a','function':{'name':'native_tool','arguments':'{}'}}]},
        {'role':'tool','tool_call_id':'a','content':json.dumps(original)},
        {'role':'assistant','tool_calls':[{'id':'b','function':{'name':'other','arguments':'{}'}}]},
        {'role':'tool','tool_call_id':'b','content':'{}'}]
    compacted,_,info=fit_request(messages,[],16384,4096)
    assert info['compacted']
    assert not any(m.get('tool_call_id')=='a' for m in compacted)
    assert json.loads(compacted[2]['content'])['review_evidence']['items'][0]['id']==identity
    assert store.read(identity)['result']==original

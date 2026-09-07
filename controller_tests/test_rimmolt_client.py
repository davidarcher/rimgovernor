import importlib.util
from pathlib import Path

spec=importlib.util.spec_from_file_location('rimmolt_client',Path(__file__).parents[1]/'scripts/benchmark_rimmolt.py')
client=importlib.util.module_from_spec(spec);spec.loader.exec_module(client)


def test_stream_assembly_keeps_calls_and_reasoning_separate():
    chunks=[{'choices':[{'delta':{'reasoning_content':'Inspect first'}}]},
            {'choices':[{'delta':{'tool_calls':[{'index':0,'id':'a','function':{'name':'get_status','arguments':'{'}}]}}]},
            {'choices':[{'delta':{'tool_calls':[{'index':0,'function':{'arguments':'}'}}]},'finish_reason':'tool_calls'}]},
            {'choices':[],'usage':{'prompt_tokens':500,'completion_tokens':25}}]
    message,reasoning,usage,finish=client.assemble(chunks)
    assert message['tool_calls']==[{'id':'a','type':'function','function':{'name':'get_status','arguments':'{}'}}]
    assert 'reasoning_content' not in message and reasoning=='Inspect first'
    assert finish=='tool_calls' and usage['prompt_tokens']==500


def test_truncated_stream_preserves_finish_status():
    assert client.assemble([{'choices':[{'delta':{'content':'unfinished'},'finish_reason':'length'}]}])[3]=='length'

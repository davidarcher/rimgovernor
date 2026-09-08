import json
import httpx
import pytest
from rimbot.model import LocalModel, ModelError
from rimbot.config import Settings


def test_inference_grammar_preserves_controller_validation():
    from rimbot.model import inference_tools
    from rimbot.colony_plan import Decision
    from rimbot.consultation import structured_tool
    from jsonschema import Draft202012Validator, ValidationError
    original = [structured_tool('commit_plan', 'Commit', Decision.model_json_schema())]
    wire = inference_tools(original)
    assert 'maxLength' not in json.dumps(wire)
    assert 'maxLength' in json.dumps(original)
    invalid = dict(expected_revision=0, disposition='continue', assessment='x',
                   rationale='x', reply='x'*1801)
    with pytest.raises(ValidationError):
        Draft202012Validator(original[0]['function']['parameters']).validate(invalid)

async def test_local_qwen_stream_on_off_and_output_limit():
    bodies=[]
    def respond(r):
        bodies.append(json.loads(r.content))
        return httpx.Response(200,text='data: '+json.dumps({'choices':[{'delta':{'reasoning_content':'plan'},'finish_reason':'length'}]})+'\n\ndata: [DONE]\n\n')
    model=LocalModel(Settings(),transport=httpx.MockTransport(respond))
    async def progress(_):pass
    for thinking in (True,False):
        with pytest.raises(ModelError,match='output limit'):await model.complete([],[],thinking,progress)
    assert [b['reasoning_effort'] for b in bodies]==['medium','none']
    assert [b['chat_template_kwargs']['enable_thinking'] for b in bodies]==[True,False]
    await model.close()

async def test_reasoning_legacy_rejection_negotiates_once():
    bodies=[]
    def respond(r):
        body=json.loads(r.content);bodies.append(body)
        if body['reasoning_effort']=='medium':
            return httpx.Response(400,json={'error':{'message':"Invalid reasoning_effort. Supported values: on, off."}})
        return httpx.Response(200,text='data: '+json.dumps({'choices':[{'delta':{'content':'Ready'},'finish_reason':'stop'}]})+'\n\ndata: [DONE]\n\n')
    model=LocalModel(Settings(),transport=httpx.MockTransport(respond))
    async def progress(_):pass
    for thinking in (True,False):
        response,_=await model.complete([],[],thinking,progress)
        assert response['content']=='Ready'
    assert [b['reasoning_effort'] for b in bodies]==['medium','on','off']
    await model.close()

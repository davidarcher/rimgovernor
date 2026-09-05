import asyncio
import json
from pathlib import Path
import httpx
import pytest
import subprocess
import sys
from pydantic import ValidationError
from rimbot.catalog import Catalog
from rimbot.native_client import NativeClient, NativeIntegrationError
from rimbot.native_models import ConstructionRequest, ConstructionResult, DefinitionQuery
from rimbot.contracts import Action,Proposal
from openapi_spec_validator import validate

def manifest():
    return json.loads((Path(__file__).parents[1]/'controller/rimbot/data/construction_contracts.json').read_text())

def catalog():
    c=Catalog();c.discovered=True;c.install_contracts(manifest());return c

def request():
    return ConstructionRequest.model_validate({'map_id':0,'buildings':[{'def_name':'Bed','stuff_def_name':'WoodLog','position':{'x':10,'z':10},'rotation':0}]})

def test_strict_domain_types_reject_coercion_and_unknown_fields():
    with pytest.raises(ValidationError):DefinitionQuery(search='bed',offset='0',limit=4)
    with pytest.raises(ValidationError):DefinitionQuery(search='bed',offset=0,limit=4,fields=['defName'])
    with pytest.raises(ValidationError):ConstructionResult(accepted=True,items=[{'state':'maybe'}])
    with pytest.raises(ValidationError):DefinitionQuery(search='bed',offset=0,limit=1000)

def test_generated_artifacts_match_separately_authored_schema():
    root=Path(__file__).parents[1]
    validate(json.loads((root/'integrations/RIMAPI/Contracts/construction.openapi.json').read_text()))
    subprocess.run([sys.executable,str(root/'scripts/generate_native_models.py'),'--check'],cwd=root,check=True,capture_output=True)

def test_native_schema_drift_fails_closed():
    m=manifest();m['endpoints'][0]['response_schema']['properties']['invented']={'type':'string'}
    with pytest.raises(ValueError,match='contract changed'):Catalog().install_contracts(m)

async def test_native_client_preserves_domain_response_and_rejects_bad_server():
    good={'accepted':True,'items':[{'placement':request().buildings[0].model_dump(),'state':'ready','thing_id':None,'reason':''}]}
    async with httpx.AsyncClient(base_url='http://test',transport=httpx.MockTransport(lambda r:httpx.Response(200,json=good))) as http:
        client=NativeClient(http,asyncio.Lock(),catalog(),lambda:None)
        response=await client.inspect(request())
        assert isinstance(response,ConstructionResult) and response.model_dump()==good
        good['arbitrary']='not in native DTO'
        with pytest.raises(NativeIntegrationError,match='violated'):await client.inspect(request())

async def test_native_construction_needs_no_model_authored_done_query(colony):
    rt,_=colony;rt.catalog.install_contracts(manifest())
    action=Action(title='Place a bed',endpoint='construction_place',arguments=request().model_dump())
    # Shape validation belongs to the DTO. Placement legality is checked by the game.
    assert rt.planner.validate_submission('Infrastructure',Proposal(summary='Bed',actions=[action])).actions[0].done is None

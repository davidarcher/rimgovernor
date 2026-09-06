from types import SimpleNamespace as NS
from unittest.mock import AsyncMock
import httpx
import pytest
from rimbot.server import create_app
from rimbot.spatial import display_label

@pytest.mark.asyncio
async def test_preview_rebuilds_after_restart_and_scopes_cache():
    area={'cells':[{'position':{'x':0,'z':0},'fertility':1,'walkable':True,'roofed':False,'zone_id':None,'thing_ids':[]}]}
    rt=NS(memory={'spatial_layout':{'regions':[]},'colony_focus':{'x':0,'z':0}},observation={'map':{'id':7}},colony='a',connected=True,api=NS(call=AsyncMock(return_value=NS(model_dump=lambda:area))))
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://testserver') as client:
        response=await client.get('/api/spatial/image')
        assert response.status_code==200
        assert response.content.startswith(b'\x89PNG')
        await client.get('/api/spatial/image')
        assert rt.api.call.await_count==1
        rt.colony='b'
        await client.get('/api/spatial/image')
        assert rt.api.call.await_count==2
        rt.memory['spatial_layout']={'regions':[],'summary':'changed'}
        await client.get('/api/spatial/image')
        assert rt.api.call.await_count==3
        rt.memory.clear()
        assert (await client.get('/api/spatial/image')).status_code==404

def test_plain_area_names():
    assert display_label('RimBot: Kitchen for Project ashf78asfgh87af')=='Kitchen'
    assert display_label('Rice field')=='Rice field'

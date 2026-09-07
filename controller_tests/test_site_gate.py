from types import SimpleNamespace as NS
from unittest.mock import AsyncMock,Mock
import pytest

async def test_no_site_does_not_call_executor(monkeypatch):
    from rimbot.semantic import execute_projects
    monkeypatch.setattr('rimbot.spatial.prepare_layout',AsyncMock())
    project={'project_id':'room','kind':'construction'}
    rt=NS(last_tick=0,memory={'spatial_layout':{'regions':[{'id':'farm','project_ids':['food'],'purpose':'farm'}],'deferred':{'room':'Invalid room footprint'}},'projects':[project]},resume_initial_planning=AsyncMock(),check_generation=Mock(),mode='automate',note=Mock(),persist=Mock(),manager_context=AsyncMock())
    await execute_projects(rt,{},[project])
    rt.manager_context.assert_not_awaited()
    assert project['status']=='needs_review'
    assert 'Waiting for architect' in project['feedback'][0]

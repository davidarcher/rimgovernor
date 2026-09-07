from types import SimpleNamespace
from unittest.mock import Mock
from rimbot.admin_requests import request_review,snapshot,acknowledge,ready,retry_later


def test_requests_coalesce_without_overwriting_other_projects():
    rt=SimpleNamespace(memory={},last_tick=100,persist=Mock())
    request_review(rt,'a:deadline','Winter food')
    request_review(rt,'b:policy','Need coverage')
    request_review(rt,'a:deadline','Winter food')
    assert rt.persist.call_count==2
    assert len(snapshot(rt))==2 and ready(rt)


def test_acknowledgement_preserves_new_or_changed_requests_during_review():
    rt=SimpleNamespace(memory={},last_tick=100,persist=Mock())
    request_review(rt,'a','Original');request_review(rt,'b','Reviewed')
    seen=snapshot(rt)
    request_review(rt,'a','Revised');request_review(rt,'c','New')
    acknowledge(rt,seen)
    assert snapshot(rt)=={'a':'Revised','c':'New'}
    acknowledge(rt,snapshot(rt));assert not ready(rt)


def test_failed_review_retains_requests_with_game_time_backoff():
    rt=SimpleNamespace(memory={},last_tick=100,persist=Mock())
    request_review(rt,'a','Original');retry_later(rt)
    assert not ready(rt) and snapshot(rt)=={'a':'Original'}
    rt.last_tick=2599;assert not ready(rt)
    rt.last_tick=2600;assert ready(rt)
    retry_later(rt);request_review(rt,'a','Original');assert not ready(rt)
    request_review(rt,'b','New problem');assert ready(rt)


async def test_request_arriving_during_administrator_turn_survives(colony,monkeypatch):
    from unittest.mock import AsyncMock
    from rimbot.semantic import semantic_review,retain_project
    from rimbot.semantic_models import WorkObjective,ObjectiveDecision
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    project=retain_project(rt.memory,'Development',WorkObjective(kind='research',outcome='Research',success_signals=['Technology unlocked']))
    request_review(rt,'first','Review research')
    async def decide(*args):
        request_review(rt,'late','New issue during inference')
        return ObjectiveDecision(response='Keep research',keep_projects=[project['project_id']])
    rt.planner.ask=AsyncMock(side_effect=decide)
    monkeypatch.setattr('rimbot.semantic.execute_projects',AsyncMock())
    await semantic_review(rt,{},[])
    assert snapshot(rt)=={'late':'New issue during inference'}


async def test_pending_request_wakes_poll_loop_without_game_events(colony,monkeypatch):
    import asyncio,time,pytest
    from unittest.mock import AsyncMock
    rt,_=colony;rt.mode='automate';rt.events_pending=[]
    rt.last_review=rt.observation['game']['game_tick'];rt.last_review_wall=time.monotonic()-16
    request_review(rt,'policy','Review coverage')
    rt.poll=AsyncMock();rt.launch_review=Mock()
    with monkeypatch.context() as patch:
        patch.setattr('rimbot.runtime.asyncio.sleep',AsyncMock(side_effect=asyncio.CancelledError))
        with pytest.raises(asyncio.CancelledError):await rt.poll_loop()
    rt.launch_review.assert_called_once()

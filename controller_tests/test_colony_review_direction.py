import pytest

from rimgovernor.colony_skills import SkillBlocked
from test_colony_controller import Replay


@pytest.mark.parametrize('refused',[False,True])
async def test_native_event_during_method_selection_requires_another_review(refused):
    rt=Replay()
    interrupted=False
    async def compile_after_event(*args):
        nonlocal interrupted
        if not interrupted:
            interrupted=True
            # Runtime's native event receiver invalidates the old read and resume.
            rt.chat_revision+=1
            rt.resume_after_review=False
            rt.wake.set()
            if refused:raise SkillBlocked('Refusal from the old observation')
        return None
    rt.controller.skills.compile=compile_after_event
    await rt.controller.cycle()
    assert rt.handled_revision==0
    assert rt.wake.is_set()
    assert not any(g.reason=='Refusal from the old observation'
                   for g in rt.current_plan.colony_goals.values())
    await rt.controller.cycle()
    assert rt.handled_revision==1 and not rt.wake.is_set()

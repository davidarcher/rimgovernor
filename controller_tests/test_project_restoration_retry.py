from copy import deepcopy
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import Decision, Failure
from test_strategic_architecture import runtime, batch
from test_zone_project_postconditions import fixture


@pytest.mark.parametrize('condition', ['restored', 'changed', 'unknown', 'direction'])
async def test_explicit_project_retry_requires_fresh_exact_native_restoration(tmp_path, condition):
    rt = runtime(tmp_path)
    await rt.sync_identity()
    rt.batch = batch()
    target, census = fixture()
    rt.current_plan, rt.projects = target.current_plan, target.projects
    progress = rt.current_plan.progress['field']
    progress.state = 'blocked'
    progress.failure = Failure(code='plan_invalidated', detail='Player edited crop')
    receipts = deepcopy(progress.issued)
    async def query(name, **args):
        if name == 'home/colony_identity': return dict(colonyId='test', mapId=1, loadToken='load')
        if condition == 'changed': census['zones'][0]['plantDef'] = 'Plant_Corn'
        if condition == 'unknown': census['zones'][0].pop('gridCells', None)
        if condition == 'direction': rt.chat_revision += 1
        return census
    rt.game.query = AsyncMock(side_effect=query)
    decision = Decision(expected_revision=rt.current_plan.revision, disposition='continue',
        assessment='Player restored field', rationale='Explicit retry', reply='Retry restored field', retry_steps=['field'])
    try:
        if condition == 'restored':
            await rt.commit_strategy(decision, actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            assert progress.state == 'pending' and progress.failure is None
            rt.manual_requests = [('field', rt.context_token, rt.chat_revision)]
            await rt.execute_manual_requests()
            assert progress.state == 'complete'
        else:
            with pytest.raises(ValueError, match='Restore the exact|direction changed'):
                await rt.commit_strategy(decision, actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            assert progress.state == 'blocked'
        assert progress.issued == receipts
        rt.game.invoke.assert_not_awaited()
    finally:
        rt.store.close()

from types import SimpleNamespace
import pytest
from rimbot.projects import ProjectBook


@pytest.mark.asyncio
async def test_installation_tracks_identity_destination_rotation_and_removal():
    book = ProjectBook()
    project = book.upsert({'title': 'Reuse bed', 'targets': [
        {'kind': 'installation', 'thing_id': 'Thing_Bed1', 'x': 10, 'z': 12, 'rotation': 1}]})
    result = {}
    async def invoke(name, args):
        assert name == 'home/install' and args == {'thingId': 'Thing_Bed1', 'dryRun': True}
        return result
    game = SimpleNamespace(invoke=invoke)
    result.update(thingId='Thing_Bed1', state='queued', blueprint={'x': 10, 'z': 12, 'rotation': 1})
    await book.reconcile(game)
    assert project.state == 'pending'
    result.update(state='installed', position={'x': 10, 'z': 12}, rotation=1, blueprint=None)
    await book.reconcile(game)
    assert project.state == 'complete' and project.matched_ids == ['Thing_Bed1']
    result['rotation'] = 0
    await book.reconcile(game)
    assert project.state == 'planned' and not project.matched_ids
    result.update(rotation=1, thingId='Thing_Bed2')
    await book.reconcile(game)
    assert project.state == 'blocked'
    result.update(thingId='Thing_Bed1', state='packed', position=None, blueprint=None)
    await book.reconcile(game)
    assert project.state == 'planned'

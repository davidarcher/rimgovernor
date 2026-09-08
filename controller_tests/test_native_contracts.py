from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.native_contracts import validate_arguments, validate_native_steps
from rimbot.colony_plan import PlanSpec


def test_argument_failure_does_not_repeat_large_schema():
    schema = {'type':'object','properties':{'count':{'type':'integer','description':'x'*30000}}}
    with pytest.raises(ValueError, match='count') as error:
        validate_arguments('native/tool', schema, {'count':'many'})
    assert len(str(error.value)) < 200


@pytest.mark.asyncio
async def test_valid_native_plan_only_reads_contract():
    game = SimpleNamespace(describe=AsyncMock(return_value={'type':'object',
        'properties':{'pawn':{'type':'string'}},'required':['pawn'],'additionalProperties':False}),
        invoke=AsyncMock())
    spec = PlanSpec(steps=[dict(id='configure',title='Configure',completion_criteria='Accepted',
        action=dict(kind='native_operation',tool='home/pawn_config',arguments={'pawn':'Thing_Human1'}))])
    await validate_native_steps(spec, game)
    game.describe.assert_awaited_once_with('home/pawn_config')
    game.invoke.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('action',[None,'list','recipes','add'])
async def test_bill_execution_rejects_implicit_or_read_only_action(action):
    game=SimpleNamespace(describe=AsyncMock(return_value={'type':'object'}),invoke=AsyncMock())
    args={'bench':'ButcherSpot1','recipe':'ButcherCorpseFlesh'}
    if action is not None: args['action']=action
    spec=PlanSpec(steps=[dict(id='bill',title='Bill',completion_criteria='Observed',
        action=dict(kind='native_operation',tool='home/bills',arguments=args))])
    if action=='add':
        await validate_native_steps(spec,game)
    else:
        with pytest.raises(ValueError,match='Bill execution needs an explicit'):
            await validate_native_steps(spec,game)
    game.invoke.assert_not_awaited()

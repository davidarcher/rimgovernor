"""Validate against discovered native contracts without dumping schemas into errors."""
from jsonschema import Draft202012Validator
from .colony_plan import NativeOperation


BILL_WRITE_ACTIONS = ('add', 'set', 'delete', 'move')


def validate_arguments(tool, schema, arguments):
    error = next(Draft202012Validator(schema).iter_errors(arguments), None)
    if error is not None:
        path = '.'.join(str(part) for part in error.absolute_path) or 'arguments'
        raise ValueError(f'{tool}: {path}: {error.message}. Read describe for this tool before retrying.')


async def validate_native_steps(spec, game):
    for step in spec.steps:
        if isinstance(step.action, NativeOperation):
            schema = await game.describe(step.action.tool)
            try:
                validate_arguments(step.action.tool, schema, step.action.arguments)
                if step.action.tool == 'home/bills' and step.action.arguments.get('action') not in BILL_WRITE_ACTIONS:
                    raise ValueError('Bill execution needs an explicit add, set, delete or move action; list and recipes belong in inspection')
                if step.action.tool == 'home/install' and any(k not in step.action.arguments for k in ('thingId', 'x', 'z')):
                    raise ValueError('Installation execution needs thingId, x and z; status queries belong in inspection')
            except ValueError as error:
                raise ValueError(f'Plan step {step.id}: {error}') from error

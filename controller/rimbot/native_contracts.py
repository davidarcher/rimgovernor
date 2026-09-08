"""Validate against discovered native contracts without dumping schemas into errors."""
from jsonschema import Draft202012Validator
from .colony_plan import NativeOperation


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
            except ValueError as error:
                raise ValueError(f'Plan step {step.id}: {error}') from error

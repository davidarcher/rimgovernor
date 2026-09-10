"""Validate against discovered native contracts without dumping schemas into errors."""
from jsonschema import Draft202012Validator
from .colony_plan import NativeOperation, StandDown


BILL_WRITE_ACTIONS = ('add', 'set', 'delete', 'move')


class NativeNotDispatched(ValueError):
    """The requested action did not reach the native invocation boundary."""


def validate_stand_down_steps(spec, current, owners, token, pawns):
    """New cleanup intent needs an owned target or an explicit future draft."""
    old = {step.id: step for step in current.spec.steps}
    proposed = {step.id: step for step in spec.steps}
    undrafted = {pawn.thing_id for pawn in pawns if pawn.drafted is False}

    def future_drafts(step):
        pending = [dependency.step for dependency in step.after]
        seen, targets, releases = set(), set(), set()
        while pending:
            identity = pending.pop()
            if identity in seen:
                continue
            seen.add(identity)
            prerequisite = proposed[identity]
            pending.extend(dependency.step for dependency in prerequisite.after)
            action = prerequisite.action
            if isinstance(action, StandDown):
                releases.update(action.pawn_ids)
            elif (isinstance(action, NativeOperation) and action.tool == 'home/order'
                  and action.arguments.get('action') == 'undraft'):
                releases.add(action.arguments.get('pawn'))
            progress = current.progress.get(identity)
            if progress and (progress.state not in ('pending', 'executing') or
                             any(receipt.get('confirmed') for receipt in progress.issued.values())):
                continue
            if (isinstance(action, NativeOperation) and action.tool == 'home/order'
                    and action.arguments.get('action') in ('draft', 'goto', 'attack', 'tend')
                    and action.arguments.get('pawn') in undrafted):
                targets.add(action.arguments['pawn'])
        return targets - releases

    for step in spec.steps:
        if not isinstance(step.action, StandDown):
            continue
        if step.id in old and step.signature() == old[step.id].signature():
            continue  # Previously accepted cleanup remains idempotent after release.
        owned = {pawn for pawn in step.action.pawn_ids if token and owners.get(pawn) == token}
        if not owned and not set(step.action.pawn_ids) & future_drafts(step):
            raise ValueError(f'Plan step {step.id}: stand_down has no current-load AI-owned target. '
                'Select an observed owned pawn or depend explicitly on a pending draft/order '
                'for that exact currently undrafted pawn. stand_down only releases drafts.')


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

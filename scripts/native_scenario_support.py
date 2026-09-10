"""Ordinary setup shared by native scenario probes."""
from rimbot.colony_plan import ColonyGoal, CommitSteps
import asyncio
import re


def baseline_tick(path):
    # RimWorld saves can contain non-XML element names in mod data. Read only the
    # game's tick-manager scalar, requiring one unambiguous nonnegative value.
    matches = re.findall(rb'<tickManager>\s*<ticksGame>(\d+)</ticksGame>', path.read_bytes())
    if len(matches) != 1:
        raise ValueError('Expected one saved tick-manager value')
    return int(matches[0])


async def allow_starting_supplies(rt):
    observed = await rt.game.query('home/colony_facts', planning=True)
    for _ in range(32):
        if not observed.get('forbiddenSupplies'):
            return observed
        goal = rt.current_plan.colony_goals.setdefault('AllowStartingSupplies', ColonyGoal(priority_class=2, source='PLAYER'))
        method, actions = await rt.controller.skills.compile('AllowStartingSupplies', observed, [])
        steps, _ = rt.controller.skills.steps('AllowStartingSupplies', method, actions, observed)
        if not steps:
            raise ValueError('Starting-supply fixture setup produced no scoped actions')
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Allow ordinary starting supplies', steps=steps).decision(rt.current_plan), actor='strategist',
            expected_token=rt.context_token, expected_revision=rt.chat_revision)
        for _ in steps:
            rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps
                                      if rt.current_plan.progress[s.id].state=='pending')
            await rt.execute_manual_requests()
        await settle_dispatch(rt, steps)
        unfinished = {s.id:rt.current_plan.progress[s.id].model_dump(mode='json') for s in steps
                      if rt.current_plan.progress[s.id].state!='complete'}
        if unfinished:
            details = str(unfinished)
            if 'not connected via GABP' in details or 'claim runtime ownership' in details:
                raise RuntimeError('Native setup infrastructure failed: '+details)
            raise AssertionError('Starting-supply setup did not complete: '+details)
        goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
        observed = await rt.game.query('home/colony_facts', planning=True)
    raise ValueError('Starting-supply fixture setup exceeded its bounded batches')


async def settle_dispatch(rt, steps):
    """Wait for already-dispatched controller calls; never issue another order."""
    async with asyncio.timeout(60):
        while any(rt.current_plan.progress[s.id].state == 'executing' for s in steps):
            if not rt.connected:
                raise RuntimeError('Native connection lost while observing in-flight dispatch')
            await asyncio.sleep(.1)

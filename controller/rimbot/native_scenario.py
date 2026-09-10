"""Test-only bounded simulation; production clock policy is unchanged."""
import asyncio


class ScenarioInterrupted(AssertionError):
    """The scenario stopped with retained native evidence."""


async def advance_game(rt, ticks, report, *, timeout=120, expected_letters=(('Ancient danger', 'ThreatBig'),), combat_targets=()):
    """Advance exactly ticks, acknowledging only inspected fixture warnings.

    Pass expected_letters=() for interruption acceptance. This operation never
    dismisses letters, clears player holds, or retries game orders. Combat windows
    accept only the exact targets already committed by the shared defense plan;
    unexpected injury and new-threat stops still raise with retained evidence.
    """
    if type(ticks) is not int or not 1 <= ticks <= 1800000:
        raise ValueError('ticks must be 1..1800000')
    clock_arguments = {}
    if combat_targets:
        committed = rt.current_plan.control.get('combat', {}).get('targets', [])
        if (not all(isinstance(target, str) and target for target in combat_targets)
                or len(set(combat_targets)) != len(combat_targets) or set(combat_targets) != set(committed)):
            raise ValueError('Combat waits require the exact committed defense targets')
        clock_arguments = dict(mode='combat', ignored_hostiles=','.join(combat_targets))
    evidence = {'ticks': ticks, 'windows': [], 'interruptions': []}
    report.setdefault('simulation', []).append(evidence)

    def require(condition, reason):
        if not condition:
            evidence['failure'] = reason
            raise ScenarioInterrupted(f'{reason}: {evidence}')

    async def identity_now():
        value = await rt.game.query('home/colony_identity')
        require(all(value.get(k) is not None for k in ('colonyId', 'mapId', 'loadToken')),
                'Native identity unavailable')
        return {k: value[k] for k in ('colonyId', 'mapId', 'loadToken')}

    if rt.review_task and not rt.review_task.done():
        await rt.review_task
    async with rt.lock, asyncio.timeout(timeout):
        identity = await identity_now()
        supervisor = rt.supervisor
        remaining, seen = ticks, set()
        previous_tick = None
        try:
            while remaining:
                require(await identity_now() == identity,
                        'Native identity changed')
                started = await supervisor.change('Superfast', max_ticks=remaining, **clock_arguments)
                require(previous_tick is None or started['startTick'] == previous_tick,
                        'Native ticks changed between windows')
                cursor = started['newestCursor']
                while True:
                    state = await supervisor.call(op='status')
                    if not state['active']:
                        break
                    await asyncio.sleep(.1)
                evidence['windows'].append(state)
                require(await identity_now() == identity, 'Native identity changed')
                require(state.get('owner') == supervisor.owner and state.get('epoch') == started['epoch'],
                        'Clock ownership changed')
                require(state.get('pauseVerified') is True, 'Native pause unverified')
                advanced = state['lastTick'] - started['startTick']
                require(0 <= advanced <= remaining, 'Invalid native tick progress')
                remaining -= advanced
                previous_tick = state['lastTick']
                if state.get('stopReason') == 'tick_budget':
                    require(remaining == 0, 'Native tick budget ended early')
                    # Deliver this window's event before a caller captures a new
                    # decision revision. Deferred delivery would stale that decision.
                    rt.clock_events.extend(await supervisor.poll())
                    rt.receive_clock_events()
                    require(not supervisor.hold, 'External clock hold')
                    evidence['completed'] = True
                    return state

                detail = {'clock': state, 'events': []}
                evidence['interruptions'].append(detail)
                require(state.get('stopReason') == 'letter_pause', 'Unexpected native interruption')
                while True:
                    batch = await supervisor.call(op='events', afterCursor=cursor, limit=128)
                    detail['events'].extend(batch['events'])
                    require(not batch.get('gap'), 'Native event history incomplete')
                    if batch['nextCursor'] == cursor:
                        break
                    cursor = batch['nextCursor']
                stops = [e for e in detail['events'] if e.get('epoch') == state['epoch']
                         and e.get('kind') == 'letter_pause']
                require(len(stops) == 1, 'Triggering letter unavailable')
                trigger = stops[0].get('event') or {}
                letter_id = trigger.get('letterId')
                require(trigger.get('source') == 'LetterStack.ReceiveLetter' and letter_id
                        and letter_id not in seen, 'Letter attribution unavailable or repeated')
                status = await rt.game.query('home/status', colonists=True, threats=True)
                detail['status'] = status
                require(status.get('skipped') == [] and status.get('blocks', {}).get('colonists') is True
                        and status.get('blocks', {}).get('threats') is True, 'Safety observation incomplete')
                letters = [row for row in status.get('letters', []) if row.get('id') == letter_id]
                require(len(letters) == 1 and (letters[0].get('label'), letters[0].get('letterDef'))
                        in expected_letters, 'Letter is not approved for this scenario')
                require(status.get('ui', {}).get('modalOpen') is False
                        and status.get('time', {}).get('paused') is True, 'Modal or unverified pause')
                counts = status.get('counts', {})
                require(all(counts.get(k) == 0 for k in ('hostileCount', 'huntingPredatorCount', 'downedCount')),
                        'Active threat or downed colonist')
                pawns = status.get('colonists')
                require(isinstance(pawns, list) and bool(pawns)
                        and all(all(p.get(k) is False for k in ('dead', 'downed', 'bleeding')) for p in pawns),
                        'Colonist safety unverified')
                require(await identity_now() == identity, 'Native identity changed')
                fresh = await supervisor.call(op='status')
                require(all(fresh.get(k) == state.get(k) for k in
                            ('owner', 'epoch', 'lastTick', 'stopReason', 'newestCursor'))
                        and fresh.get('active') is False, 'Clock changed during inspection')
                seen.add(letter_id)
                detail['acknowledgedLetterId'] = letter_id
                rt.note('scenario_interruption', 'Acknowledged inspected fixture warning', **detail)
                # Consume the event through normal runtime invalidation before
                # starting another lease. Never release an external/player hold.
                rt.clock_events.extend(await supervisor.poll())
                rt.receive_clock_events()
                require(not supervisor.hold, 'External clock hold')
            evidence['completed'] = True
            return state
        except BaseException as error:
            evidence.setdefault('failure', str(error) or type(error).__name__)
            # A timeout must not leave this scenario's lease advancing. Never
            # stop another owner or a newly loaded game during cleanup.
            try:
                if await identity_now() == identity:
                    current = await supervisor.call(op='status')
                    if current.get('active') and current.get('owner') == supervisor.owner:
                        evidence['cleanup'] = await supervisor.call(op='pause', owner=supervisor.owner,
                                                                    epoch=current['epoch'])
            except Exception as cleanup_error:
                evidence['cleanupFailure'] = str(cleanup_error)
            raise

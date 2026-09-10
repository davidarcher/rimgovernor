"""Sample retained native evidence; never issue extra game reads or orders."""
import time
from copy import deepcopy


class StartupMilestones:
    def __init__(self, rt):
        self.started = time.monotonic()
        self.context = rt.context_token
        self.initial_tick = rt.batch.summary.end_tick
        self.previous_tick = self.initial_tick
        self.previous_pawns = {}
        self.baseline_buildings = {
            b['thingId'] for b in rt.batch.native.get('buildings', {}).get('buildings', [])
            if b.get('status') == 'built'}
        self.rows = {}
        self.valid = True
        self.sample(rt)

    def mark(self, name, tick, **evidence):
        self.rows.setdefault(name, dict(seconds=time.monotonic()-self.started,
                                       observed_tick=tick, **evidence))

    def sample(self, rt):
        tick = rt.batch.summary.end_tick
        if rt.context_token != self.context or tick < self.previous_tick:
            self.valid = False
        if not self.valid:
            return
        clocks = [tick]
        if type(rt.clock.get('ticksGame')) is int:
            clocks.append(rt.clock['ticksGame'])
        if max(clocks) > self.initial_tick:
            self.mark('first_observed_tick', max(clocks))
        if rt.execution_window_end is not None:
            self.mark('first_execution_window', tick, deadline=rt.execution_window_end)
        if rt.current_plan.control.get('status') == 'FOOTHOLD_STABLE':
            self.mark('first_stable_foothold', tick)
        pawns = rt.batch.native.get('pawns', {}).get('pawns', [])
        work = {'DoBill', 'ConstructFinishFrame', 'ConstructDeliverResources', 'HaulToCell', 'Sow', 'Harvest'}
        for pawn in pawns:
            previous = self.previous_pawns.get(pawn['thingId'])
            if (tick > self.previous_tick and previous and pawn.get('job') in work
                    and previous.get('job') == pawn.get('job')
                    and (previous.get('position') != pawn.get('position')
                         or previous.get('carriedThingId') != pawn.get('carriedThingId'))):
                self.mark('first_observed_pawn_work', tick, pawn=pawn['thingId'], job=pawn['job'])
        for building in rt.batch.native.get('buildings', {}).get('buildings', []):
            if (tick > self.initial_tick and building.get('status') == 'built'
                    and building['thingId'] not in self.baseline_buildings):
                self.mark('first_observed_new_building', tick, thing_id=building['thingId'])
        if tick > self.previous_tick or not self.previous_pawns:
            self.previous_pawns = {p['thingId']: deepcopy(p) for p in pawns}
            self.previous_tick = tick

    def report(self):
        return dict(valid=self.valid, initial_tick=self.initial_tick, milestones=self.rows,
            scope='Wall seconds since automation setup, sampled from retained native data. '
                  'Times are observation upper bounds; missing milestones are unobserved. '
                  'Pawn work requires position/carry change during the same work job. '
                  'New buildings and pawn work are not attributed to controller orders; '
                  'a stable sample is not sustained survival.')

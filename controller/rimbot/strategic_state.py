"""Deterministic signals and deliberately bounded model projections."""
import hashlib
import json


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def features(batch):
    s, native = batch.summary, batch.native
    buildings = native.get('buildings', {})
    nets = buildings.get('powerNets')
    power = None if nets is None else [{'net_w': n.get('netW'), 'stored_wd': n.get('storedWd'),
        'days_at_current_deficit': n['storedWd']/-n['netW'] if n.get('netW', 0)<0 and n.get('storedWd') is not None else None,
        'flags': n.get('flags', [])} for n in nets]
    return {'tick': s.end_tick, 'people': {'count': len(s.pawns),
        'downed': [p.thing_id for p in s.pawns if p.downed], 'dead': [p.thing_id for p in s.pawns if p.dead],
        'bleeding': [p.thing_id for p in s.pawns if p.bleeding], 'needs_tend': [p.thing_id for p in s.pawns if p.needs_tend],
        'no_job': [p.thing_id for p in s.pawns if p.job is None and not p.downed and not p.dead],
        'armed': sum(p.armed is True for p in s.pawns),
        'mood': {p.thing_id: p.mood for p in s.pawns}, 'food_need': {p.thing_id: p.food for p in s.pawns}},
        'resources': {'allowed_units_by_def': {r.def_name: r.owned_unforbidden_units for r in s.supplies},
            'construction_deficit': buildings.get('resourceDeficit'),
            'food_days': None, 'expected_harvest': None,
            'unknown': ['Edible nutrition, diet-adjusted consumption, spoilage and harvest forecasts are not exposed by the current compact native contract. Item counts are not nutrition.']},
        'power': power, 'construction': buildings.get('attention'),
        'space': {'visible_rooms': s.visible_rooms, 'zones': s.zone_count, 'fogged_rooms_omitted': s.fogged_rooms_omitted},
        'threats': {'hostiles': s.hostile_count, 'hunting_predators': s.hunting_predator_count},
        'alerts': sorted(s.alert_labels), 'warnings': s.warnings}


class StrategicState:
    def __init__(self, saved=None):
        saved = saved or {}
        self.memories = saved.get('memories', {})
        self.current = saved.get('current', {})
        self.previous = saved.get('previous', {})
        self.last_decision = saved.get('last_decision', {})
        self.last_decision_tick = saved.get('last_decision_tick', 0)
        self.latches = saved.get('latches', {})
        self.pending = saved.get('pending', [])
        self.trends = saved.get('trends', [])

    def dump(self):
        return dict(memories=self.memories, current=self.current, previous=self.previous, last_decision=self.last_decision,
            last_decision_tick=self.last_decision_tick, latches=self.latches, pending=self.pending, trends=self.trends)

    def signal(self, kind, evidence):
        row = {'kind': kind, 'evidence': evidence, 'tick': self.current.get('tick')}
        key = fingerprint({'kind': kind, 'evidence': evidence})
        if not any(r['key'] == key for r in self.pending):
            self.pending.append(dict(row, key=key))
        self.pending = self.pending[-60:]

    def update(self, batch):
        value = features(batch)
        self.previous, self.current = self.current, value
        old = self.previous
        if not old:
            self.signal('colony.observed', {'colonists': value['people']['count']})
        else:
            for key in ('count', 'downed', 'dead', 'bleeding', 'needs_tend'):
                if value['people'][key] != old['people'][key]:
                    self.signal('pawn.'+key, value['people'][key])
            for key in ('threats', 'alerts', 'space'):
                if value[key] != old[key]:
                    self.signal(key+'.changed', value[key])
        shortage_defs = sorted(r['defName'] for r in value['resources']['construction_deficit'] or []
            if r.get('unforbiddenOnMap') is not None and r['stillNeeded'] > r['unforbiddenOnMap'])
        if shortage_defs != self.latches.get('material_shortages', []):
            self.signal('resource.shortage_changed', shortage_defs)
        self.latches['material_shortages'] = shortage_defs
        low_power = any(n['days_at_current_deficit'] is not None and n['days_at_current_deficit'] < .25 for n in value['power'] or [])
        if low_power != self.latches.get('low_power', False):
            self.signal('power.reserve_low' if low_power else 'power.reserve_recovered', value['power'])
        self.latches['low_power'] = low_power
        # Explicit enter/exit thresholds prevent mood and hunger boundary chatter.
        for field, low, recovered in (('mood', .25, .35), ('food_need', .2, .4)):
            for pawn, amount in value['people'][field].items():
                if amount is None:
                    continue
                key = field+':'+pawn
                was = self.latches.get(key, False)
                now = amount < (recovered if was else low)
                self.latches[key] = now
                if now != was:
                    self.signal(field+'.risk' if now else field+'.recovered', {'pawn': pawn, 'value': amount})
        # Daily checkpoint only when strategic facts changed, never on ticks alone.
        material = {k:v for k,v in value.items() if k not in ('tick', 'warnings')}
        if value['tick']-self.last_decision_tick >= 60000 and material != self.last_decision:
            self.signal('strategic.changed_since_daily_check', {})
        if not self.trends or value['tick']-self.trends[-1]['tick'] >= 2500:
            self.trends.append({'tick': value['tick'], 'people': value['people']['count'], 'power': value['power'],
                'allowed_units_by_def': value['resources']['allowed_units_by_def']})
            self.trends = self.trends[-48:]
        return bool(self.pending)

    def decided(self):
        self.last_decision = {k:v for k,v in self.current.items() if k not in ('tick', 'warnings')}
        self.last_decision_tick = self.current.get('tick', 0)
        self.pending.clear()


def bounded(rows, limit):
    return {'items': rows[:limit], 'omitted': max(0, len(rows)-limit)}


def context(rt):
    state = rt.strategic_state.current
    plan = rt.current_plan
    steps = [dict(id=s.id, title=s.title, priority=s.priority, after=[d.model_dump() for d in s.after],
        action=s.action.kind, state=plan.progress[s.id].state,
        failure=plan.progress[s.id].failure.model_dump(exclude={'evidence'}) if plan.progress[s.id].failure else None,
        issued_operations=len(plan.progress[s.id].issued), completion=s.completion_criteria) for s in plan.spec.steps]
    people = state.get('people', {})
    resources = state.get('resources', {})
    return {'colony': {'people': people, 'threats': state.get('threats'), 'alerts': state.get('alerts'),
        'space': state.get('space'), 'power': state.get('power'), 'construction': state.get('construction'),
        'resources': {k:v for k,v in resources.items() if k != 'allowed_units_by_def'},
        'available_supply_definitions': len(resources.get('allowed_units_by_def', {})), 'warnings': state.get('warnings')},
        'current_plan': {'revision': plan.revision, 'chosen_tick': plan.chosen_tick, 'rationale': plan.rationale,
            'goals': plan.spec.goals, 'constraints': plan.spec.constraints, 'assumptions': plan.spec.assumptions,
            'risks': plan.spec.risks, 'long_term': plan.spec.long_term, 'right_now': plan.spec.right_now,
            'steps': bounded(steps, 24)},
        'memory_index': [{'id': key, 'recorded_tick': note['tick']} for key, note in rt.strategic_state.memories.items()],
        'changes': bounded(rt.strategic_state.pending, 20), 'mode': rt.mode,
        'ai_owned_drafts': [pawn for pawn, token in rt.draft_owners.items() if token == rt.context_token],
        'player_messages': bounded([{'kind': m['kind'], 'text': m['text']} for m in rt.chat[-12:]], 12),
        'clock': {k:v for k,v in (rt.supervisor.state if rt.supervisor else {}).items()
            if k in ('active','paused','stopReason','stopDetail','requestedSpeed')},
        'optional_roles': [role.value for role in rt.router.routing.roles if role.value != 'strategist']}


def projection(rt, sections):
    allowed = {'people', 'resources', 'power', 'construction', 'space', 'threats'}
    if not set(sections) <= allowed or not 1 <= len(sections) <= 3:
        raise ValueError('Choose one to three state sections')
    value = {'load_token': rt.context_token, 'tick': rt.strategic_state.current.get('tick'),
        'sections': {k:rt.strategic_state.current.get(k) for k in sections}}
    if 'space' in sections:
        value['local_pawns'] = bounded([p.model_dump() for p in rt.batch.summary.pawns], 16)
        value['reserved_walkways'] = [r.model_dump() for r in rt.current_plan.spec.reserved_walkways]
    # Refuse a huge projection explicitly rather than silently dropping its evidence.
    if len(json.dumps(value)) > 16000:
        raise ValueError('Projection is too large; select fewer sections or inspect filtered native details')
    return value

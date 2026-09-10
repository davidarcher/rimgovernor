"""Native extraction facilities are staged through shared construction commitments."""
from .strategic_state import fingerprint


def facility_key(site):
    return 'extraction-facility-' + fingerprint(dict(defName=site['defName'], x=site['x'], z=site['z'],
        rotation=site.get('rotation', 'north')))[:16]


def drilling_policy(plan):
    facilities = {}
    for goal in plan.colony_goals.values():
        resource = goal.target.get('resource')
        if not resource:
            continue
        for site in goal.evidence.get('drilling_facilities', []):
            # Only accepted shared-plan placements authorize native admission.
            committed = any(step.goal_id and plan.colony_goals.get(step.goal_id) is goal
                and plan.progress[step.id].state != 'cancelled'
                and step.action.kind == 'place_buildings'
                and any(p.def_name == site['defName'] and p.x == site['x'] and p.z == site['z']
                        and plan.progress[step.id].issued.get(str(index), {}).get('confirmed')
                        for index, p in enumerate(step.action.placements)) for step in plan.spec.steps)
            observed = any(r.get('thingId') and r['x'] == site['x'] and r['z'] == site['z']
                           and r['defName'] == site['defName'] for r in
                           (goal.evidence.get('extraction_infrastructure') or {}).get('owned', []))
            if not (committed or observed):
                continue
            target = goal.target['quantity'] if goal.target.get('deep_extraction') and not goal.cancelled and goal.status != 'blocked' else 0
            key = (site['defName'], site['x'], site['z'], resource)
            facilities[key] = target
    return ','.join('/'.join(map(str, (*key, target))) for key, target in sorted(facilities.items()))


def development_method(goal, sources, facts):
    from .colony_skills import SkillBlocked, native
    infrastructure = sources.get('infrastructure') or {}
    if not goal.target.get('deep_extraction'):
        raise SkillBlocked('Deep extraction requires explicit player approval of drilling and its native infestation risk')
    owned = infrastructure.get('owned', [])
    if any(r.get('missing') is True and r.get('depleted') is not True and goal.method_seen(facility_key(r)) for r in owned):
        raise SkillBlocked('Extraction facility was removed before depletion; inspect retained construction before renewing work')
    for drill in infrastructure.get('drills', []):
        if not any(r.get('thingId') == drill['thingId'] and r.get('depleted') is True for r in owned):
            continue
        if drill.get('switchOn') is False:
            continue
        if drill.get('switchOn') is not True or not drill.get('flickWorkers'):
            raise SkillBlocked('Extraction development cannot retire depleted equipment without a native switch and eligible worker')
        work = goal.evidence.setdefault('work_types', [])
        if infrastructure['flickWorkType'] not in work:
            work.append(infrastructure['flickWorkType'])
        method = 'retire-drill-' + drill['thingId']
        if drill.get('flickDesignated'):
            return None
        if goal.method_seen(method):
            raise SkillBlocked('Depleted drill switch-off was interrupted; inspect and explicitly renew the resource goal')
        return method, [native('home/building_config', thing=drill['thingId'], power='off', watch=False)]
    working = [d for d in infrastructure.get('drills', []) if d.get('resource') == goal.target['resource']
               and any(r.get('thingId') == d['thingId'] for r in owned)]
    if working:
        if any(d.get('forbidden') for d in working):
            raise SkillBlocked('Extraction facility was forbidden; inspect before renewing work')
        if not any(d.get('available') is True for d in working):
            raise SkillBlocked('Extraction development is waiting for native drill power or availability')
        return None
    storage = sources.get('storage') or {}
    if not storage.get('haulers'):
        raise SkillBlocked('Material storage unavailable: no eligible hauler for deep extraction')
    required = max(1, goal.target['quantity'] - goal.evidence.get('stock', 0)) + storage.get('deepPortion', 0)
    if storage.get('capacity', 0) < required:
        import math
        cells = storage.get('candidates', [])[:math.ceil((required - storage.get('capacity', 0)) / storage['stackLimit'])]
        if not cells:
            raise SkillBlocked('Material storage unavailable: no safe deep extraction storage space')
        key = 'deep-storage-' + fingerprint({'resource': goal.target['resource'], 'cells': cells})[:16]
        if goal.method_seen(key):
            raise SkillBlocked('Material storage unavailable: interrupted deep extraction storage needs inspection')
        return key, [{'kind': 'create_zone', 'zone_type': 'stockpile', 'label': 'RimBot ' + key,
            'preset': 'nothing', 'allow': [goal.target['resource']], 'priority': 'Important',
            'patches': [dict(c, width=1, height=1) for c in cells]}]
    definitions = infrastructure.get('definitions', [])
    previous = goal.evidence.get('extraction_facility')
    if (previous and goal.method_seen(facility_key(previous))
            and any(d['defName'] == previous['defName'] and d['method'] == 'scan' for d in definitions)
            and not any(s['defName'] == previous['defName'] and s.get('x') == previous['x'] and s.get('z') == previous['z']
                        for s in infrastructure.get('scanners', []))):
        raise SkillBlocked('Extraction scanner was removed; inspect and explicitly renew the resource goal')
    if not infrastructure.get('scannersActive') and infrastructure.get('scanners'):
        raise SkillBlocked('Extraction development requires the existing scanner to be powered and available; player equipment is not replaced')
    method = 'drill' if infrastructure.get('scannersActive') else 'scan'
    available = [d for d in definitions if d.get('method') == method and d.get('available') is True]
    if not available:
        goal.evidence['extraction_research'] = [r for d in definitions if d.get('method') == method for r in d.get('research', [])]
        raise SkillBlocked('Extraction development requires the observed native research prerequisites')
    # This method only schedules observed, powered native candidates. Future
    # facilities own no cells until ordinary construction preflight admits them.
    candidates = infrastructure.get('sites', [])
    for site in candidates:
        definition = next((d for d in available if d['defName'] == site['defName']), None)
        if definition is None or site.get('eligible') is not True:
            continue
        key = facility_key(site)
        if goal.method_seen(key):
            raise SkillBlocked('Extraction facility changed or was interrupted; inspect and explicitly renew the resource goal')
        facts.setdefault('definitions', {})[definition['defName']] = definition
        goal.evidence['work_types'] = site['workTypes']
        goal.evidence['extraction_facility'] = site
        if method == 'drill':
            facilities = goal.evidence.setdefault('drilling_facilities', [])
            if not any((s['defName'],s['x'],s['z']) == (site['defName'],site['x'],site['z']) for s in facilities):
                if len(facilities) >= 32:
                    raise SkillBlocked('Extraction facility limit requires inspection')
                facilities.append(site)
        return key, [{'kind': 'place_buildings', 'placements': [
            {'def_name': site['defName'], 'x': site['x'], 'z': site['z'], 'rotation': site['rotation']}]}]
    raise SkillBlocked('Extraction development has no observed site with native power, safe access, workers and required deposits')

"""Observed disruption phases and priorities within the shared colony plan."""
from .colony_policy import criteria


SERVICES = {
    'food': 'EnsureFoodSupply', 'production': 'EnsureFoodSupply',
    'sleeping': 'EnsureInitialShelter', 'shelter': 'EnsureInitialShelter',
    'temperature': 'EnsureTemperatureSafety', 'cooking': 'EnsureCooking',
    'power': 'EnsureBasicPower', 'storage': 'EnsureFoodStorage',
}


def reconcile(control, facts, policy, *, context, direction):
    """Retain uncertainty and distinguish provisionally safe from restored service.

    This record never issues actions or declares their completion. Native-derived
    service gates continue to select existing goals and verify their outcomes.
    """
    previous = control.get('disaster_recovery')
    if previous and (previous.get('context') != context or facts['tick'] < previous['observed_tick']):
        control.pop('disaster_recovery')
        previous = None
    observation = facts.get('environment')
    if not isinstance(observation, dict) or not isinstance(observation.get('conditions'), list):
        if previous and previous['phase'] != 'restored':
            previous['phase'] = 'unknown'
            previous['reason'] = 'Fresh environmental observation unavailable'
        return previous
    conditions = observation['conditions']
    if any(not isinstance(c, dict) or not c.get('defName') for c in conditions):
        if previous and previous['phase'] != 'restored':
            previous['phase'] = 'unknown'
            previous['reason'] = 'Incomplete native condition identities'
        return previous
    if previous and previous['phase'] == 'restored':
        if not conditions:
            return previous
        previous = None
    gates = criteria(facts, policy)
    deficits = [name for name in SERVICES if not gates[name]]
    if not previous and not conditions:
        return None
    if not previous or previous.get('context') != context:
        previous = control['disaster_recovery'] = {
            'context': context, 'direction': direction, 'started_tick': facts['tick'],
            'initial_stock': dict(facts.get('resources', {})), 'affected_services': [],
        }
    previous.update(
        direction=direction, observed_tick=facts['tick'], conditions=conditions,
        deficits=deficits, stock=dict(facts.get('resources', {})),
        affected_services=sorted(set(previous['affected_services']) | set(deficits)),
    )
    previous['phase'] = ('disrupted' if deficits else 'temporary_survival') if conditions else (
        'recovering' if deficits else 'restored')
    previous['reason'] = ''
    previous['service_evidence'] = {name: gates[name] for name in SERVICES}
    return previous


def prioritize(nodes, recovery):
    """Promote disrupted services without bypassing emergency or player guards."""
    if not recovery or recovery['phase'] not in ('disrupted', 'recovering'):
        return nodes
    required = {SERVICES[name] for name in recovery['deficits']}
    if {'temperature', 'cooking'} & set(recovery['deficits']):
        required.add('MaintainWood')
    return sorted(((name, min(priority, 2) if name in required else priority)
                   for name, priority in nodes), key=lambda node: node[1])

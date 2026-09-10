"""Development phases over the shared maintained goals and observed colony functions."""


def stage_layout(plan, facts, gates, nodes):
    """Reopen capacity when the colony grows; future phases own no map cells."""
    shelter=gates.get('shelter') is True and gates.get('sleeping') is True
    services=gates.get('storage') is True and gates.get('cooking') is True
    stage='capacity' if shelter and services else 'food_services' if shelter else 'habitable_shelter'
    roles={
        'sleeping':dict(goal='EnsureInitialShelter',required=facts.get('colonists'),
            observed=facts.get('indoorSleepingCapacity'),verified=shelter),
        'food_storage':dict(goal='EnsureFoodStorage',verified=gates.get('storage') is True),
        'cooking':dict(goal='EnsureCooking',verified=gates.get('cooking') is True),
        'temperature':dict(goal='EnsureTemperatureSafety',verified=gates.get('temperature') is True),
    }
    previous=plan.control.get('spatial_program',{})
    generation=previous.get('generation',0)
    if previous.get('stage')=='capacity' and not shelter:generation+=1
    # Urgent food service work can proceed while shelter is being repaired.
    deferred=[] if shelter else [identity for identity,priority in nodes
        if identity in ('EnsureFoodStorage','EnsureCooking') and priority>2]
    plan.control['spatial_program']=dict(stage=stage,generation=generation,observed_tick=facts.get('tick'),
        roles=roles,phases=[
            dict(id='habitable_shelter',goals=['EnsureInitialShelter','EnsureTemperatureSafety'],verified=shelter),
            dict(id='food_services',after='habitable_shelter',goals=['EnsureFoodStorage','EnsureCooking'],verified=shelter and services),
            dict(id='capacity',after='food_services',goals=['EnsureInitialShelter'],maintained=True,verified=shelter and services)],
        selected_shelter=plan.control.get('preferred_shelter'),
        deferred_goals=deferred)
    # Food acquisition, emergency treatment and temperature never wait for a
    # development phase. Explicit player actions continue through ordinary Hands.
    return [(identity,priority) for identity,priority in nodes
            if identity not in deferred]

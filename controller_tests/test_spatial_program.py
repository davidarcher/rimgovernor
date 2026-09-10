from rimgovernor.colony_plan import ColonyPlan
from rimgovernor.spatial_program import stage_layout


def test_services_wait_for_observed_shelter_while_food_and_emergencies_continue():
    plan=ColonyPlan()
    nodes=[('EnsureInitialShelter',2),('EnsureFoodSupply',2),('EnsureCooking',2),
        ('EnsureFoodStorage',3),('CriticalMedical',1),('EnsureTemperatureSafety',2)]
    selected=stage_layout(plan,dict(tick=100,colonists=8,indoorSleepingCapacity=0),{},nodes)
    assert {n[0] for n in selected}=={'EnsureInitialShelter','EnsureFoodSupply','EnsureCooking','CriticalMedical','EnsureTemperatureSafety'}
    assert not plan.spec.steps and plan.control['spatial_program']['stage']=='habitable_shelter'
    # An issued/completed action list without native gates cannot advance the stage.
    assert stage_layout(plan,dict(tick=200),dict(shelter=None,sleeping=True),nodes)==selected
    assert plan.control['spatial_program']['roles']['sleeping']['verified'] is False


def test_native_capacity_reopens_after_growth_and_survives_plan_persistence():
    plan=ColonyPlan();nodes=[('EnsureInitialShelter',2),('EnsureCooking',2)]
    gates=dict(shelter=True,sleeping=True,storage=True,cooking=True)
    stage_layout(plan,dict(tick=100,colonists=8,indoorSleepingCapacity=8),gates,nodes)
    assert plan.control['spatial_program']['stage']=='capacity'
    plan=ColonyPlan.model_validate_json(plan.model_dump_json())
    stage_layout(plan,dict(tick=200,colonists=10,indoorSleepingCapacity=8),dict(gates,sleeping=False),nodes)
    program=plan.control['spatial_program']
    assert program['stage']=='habitable_shelter' and program['generation']==1
    assert program['roles']['sleeping']['required']==10
    stage_layout(plan,dict(tick=300,colonists=10,indoorSleepingCapacity=8),dict(gates,sleeping=False),nodes)
    assert plan.control['spatial_program']['generation']==1
    # Capacity recovery releases service work on the same maintained goal system.
    assert stage_layout(plan,dict(tick=400,colonists=10,indoorSleepingCapacity=10),gates,nodes)==nodes

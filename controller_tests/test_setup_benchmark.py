from rimbot.benchmark import SetupMetrics


def building(id,state='built'):
    return {'thing_id':id,'state':state,'def_name':'Bed'}


def work(tick,idle=True,needed=20,done=0):
    return {'observed_tick':tick,'workers':[{'idle':idle}], 'sites':[{'def_name':'Bed','position':{'x':1,'z':2},'stage':'frame','work_done':done,'materials':[{'needed':needed}]}]}


def test_benchmark_distinguishes_queued_work_progress_and_completion():
    metrics=SetupMetrics([building(1)],['Bed'],3)
    assert not metrics.sample(work(100),[building(1),building(2,'blueprint')])
    assert not metrics.sample(work(160,needed=0),[building(1),building(2,'frame')])
    assert metrics.idle_pawn_ticks==60
    assert metrics.progress_transitions==1
    assert not metrics.completed_ids
    assert metrics.sample(work(220,False,0,100),[building(1),building(2),building(3)])
    assert metrics.completed_ids=={2,3}
    metrics.sample(work(280,False,0,100),[building(1),building(2),building(3),building(4)])
    assert metrics.peak_excess_capacity==1
    assert metrics.progress_transitions==2


def test_benchmark_no_order_is_not_zero_latency():
    metrics=SetupMetrics([],['Bed'],3)
    report=metrics.report([],100)
    assert report['first_order_seconds'] is None
    assert report['orders_issued']==0


def test_benchmark_role_costs_include_failures_and_schedule_decisions():
    report=SetupMetrics([],['Bed'],3).report([
        {'kind':'model_call','role':'Architect','seconds':12},
        {'kind':'model_failure','role':'Architect','seconds':3},
        {'kind':'executor_schedule','role':'Executor:construction','decision':'invoked'},
        {'kind':'executor_schedule','role':'Executor:construction','decision':'skipped'}],0)
    assert report['roles']['Architect']=={'calls':1,'failures':1,'seconds':15,'executor_invocations':0,'executor_skips':0}
    assert report['roles']['Executor:construction']['executor_skips']==1
    assert report['roles']['Executor:construction']['executor_invocations']==1


async def test_native_work_context_is_available_before_manager_decisions(colony):
    rt,game=colony
    context=await rt.manager_context({'assignment':'Finish existing beds'})
    assert context['construction_work']['map_id']==game.map_id
    assert context['assignment']=='Finish existing beds'


async def test_missing_work_support_is_explicitly_unknown(colony):
    rt,_=colony
    rt.catalog.available.discard('get_map_construction_work')
    context=await rt.observe_work({})
    assert context['construction_work']['available'] is False
    assert 'sites' not in context['construction_work']


def test_excess_capacity_does_not_pass():
    metrics=SetupMetrics([],['Bed'],8)
    assert not metrics.sample(work(100),[building(i) for i in range(332)])
    report=metrics.report([],0)
    assert report['observed_target_objects']==332
    assert report['usable_target_capacity'] is None
    assert report['peak_excess_target_capacity']==324


def test_receipts_separate_unknown_and_accepted_from_completion():
    from rimbot.benchmark import placement_receipts
    orders=[{'action':{'endpoint':'construction_place','arguments':{'buildings':[{}, {}, {}]}},
             'native_result':{'items':[{'state':'blueprint'},{'state':'rejected'}]}}]
    report=placement_receipts(orders)
    assert report['attempted_placements']==3
    assert report['accepted_native_placements']==1
    assert report['rejected_native_placements']==1
    assert report['unknown_placement_outcomes']==1

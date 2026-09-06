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

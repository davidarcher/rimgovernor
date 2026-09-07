from rimbot.project_outcomes import assess


def farm(**progress):
    return {'kind':'growing','crop_def':'TestCrop','target_cells':10,
            'status':'orders_verified','success_signals':['Food supply is sustainable'],
            'progress':progress}


def test_zone_receipt_does_not_prove_sowing_or_food():
    project=farm(matching_cells=10,zones=[{'crop':'TestCrop','plants_present':0}])
    result=assess(project)
    assert [c['status'] for c in result['checks']]==['met','unmet']
    assert result['review_required']==['Food supply is sustainable']
    assert project['status']=='orders_verified'


def test_missing_observations_are_unknown_not_zero_or_success():
    assert assess(farm())['status']=='unknown'
    result=assess(farm(matching_cells=10,zones=[{'crop':'TestCrop','plants_present':None}]))
    assert result['checks'][1]['status']=='unknown'


def test_other_crops_do_not_satisfy_target_and_loss_invalidates_evidence():
    project=farm(matching_cells=10,zones=[{'crop':'TestCrop','plants_present':10},
                                         {'crop':'OtherCrop','plants_present':100}])
    assert assess(project)['status']=='measured_targets_met'
    project['progress']['zones'][0]['plants_present']=2
    assert assess(project)['checks'][1]['observed']==2
    assert assess(project)['status']=='unmet'


def test_partial_roof_survey_cannot_verify_complete_coverage():
    project={'kind':'construction','progress':{'roof':{'interior_cells':20,'observed_cells':10,'roofed_cells':10}}}
    assert assess(project)['checks'][0]['status']=='unknown'
    project['progress']['roof'].update(observed_cells=20,roofed_cells=20)
    assert assess(project)['status']=='measured_targets_met'
    assert assess({'kind':'construction','progress':{}})['status']=='unknown'

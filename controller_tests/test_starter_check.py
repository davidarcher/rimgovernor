from rimbot.starter_check import assess


def fixture():
    observation={'game':{'colonist_count':2},'rooms':{'rooms':[{'touches_map_edge':False,'is_doorway':False,
        'is_prison_cell':False,'open_roof_count':0,'pawns_reaching_visible_cell':[1,2],'contained_beds_ids':[10,11]}]},
        'zones':{'zones':[{'type':'Zone_Stockpile','cells_count':12}]},
        'resources':{'critical_resources':{'food_summary':{'access':{'consumers':[{'pawn_id':1,'nutrition_per_day':1},
        {'pawn_id':2,'nutrition_per_day':1}],'pools':[{'pawn_ids':[1,2],'nutrition':2}]}}}}}
    buildings=[{'thing_id':i,'def_name':'SleepingSpot','state':'built'} for i in (10,11)]
    return observation,buildings


def test_completed_spots_need_shelter_supplies_and_stockpile():
    obs,buildings=fixture()
    assert assess(obs,buildings,['SleepingSpot'],2)['passed']
    obs['rooms']['rooms'][0]['open_roof_count']=1
    assert not assess(obs,buildings,['SleepingSpot'],2)['passed']
    obs['rooms']['rooms'][0]['open_roof_count']=0
    obs['resources']['critical_resources']['food_summary']['access']['pools']=[]
    assert not assess(obs,buildings,['SleepingSpot'],2)['passed']


def test_queued_beds_and_missing_observation_cannot_pass():
    obs,buildings=fixture();buildings[0]['state']='blueprint'
    assert not assess(obs,buildings,['SleepingSpot'],2)['passed']
    obs.pop('rooms')
    assert assess(obs,buildings,['SleepingSpot'],2)['checks']['sheltered_sleeping_objects'] is None

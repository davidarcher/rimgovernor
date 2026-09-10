from rimgovernor.food_preservation import preservation_bill


def facts():
    return dict(nutritionPerDay=5,foodForecast=dict(readable=True,atRiskNutrition=20,runwayDays=2),
        cooking=[dict(id='bench',usable=True,bills=[],production=[dict(recipe='PreserveNative',available=True,
            products=[dict(defName='NativeFood',nutrition=.5,edible=True,rotDays=60)])])])


def test_spoilage_risk_compiles_native_preservation_capacity():
    f=facts()
    assert preservation_bill(f,7)==dict(recipe='PreserveNative',bench='bench',targetCount=70,product='NativeFood')
    assert preservation_bill(f,14)['targetCount']==140
    f['foodForecast']['atRiskNutrition']=0
    assert preservation_bill(f,7) is None


def test_existing_capacity_is_preserved_and_insufficient_bill_is_not_edited():
    f=facts();bill=dict(recipe='PreserveNative',suspended=False,repeatMode='TargetCount',targetCount=70)
    f['cooking'][0]['bills']=[bill]
    assert preservation_bill(f,7) is None
    assert preservation_bill(f,14)['targetCount']==140
    assert bill['targetCount']==70
    bill['repeatMode']='Forever'
    assert preservation_bill(f,14) is None


def test_unavailable_perishable_or_disallowed_recipes_cannot_satisfy_preservation():
    for field,value in [('available',False)]:
        f=facts();f['cooking'][0]['production'][0][field]=value
        assert preservation_bill(f,7) is None
    for field,value in [('nutrition',None),('edible',False),('rotDays',2)]:
        f=facts();f['cooking'][0]['production'][0]['products'][0][field]=value
        assert preservation_bill(f,7) is None

"""Field sizing is a production budget, never a credit to stored nutrition."""
from math import ceil, isfinite


def finite(value):
    return type(value) in (int,float) and isfinite(value)


def choose_crop(facts):
    """Rank native crop output on the observed free soil within its climate budget."""
    climate=facts.get('foodClimate',{})
    if climate.get('sowingNow') is False:return None
    choices=[]
    for name in ('Plant_Rice','Plant_Potato','Plant_Corn'):
        definition=facts.get('definitions',{}).get(name,{})
        if any(definition.get(k) is False for k in ('sowingNow','available','edibleCrop')):continue
        days,yield_=definition.get('growDays'),definition.get('harvestNutrition')
        minimum=definition.get('fertilityMin')
        sensitivity=definition.get('fertilitySensitivity',1)
        if any(not finite(v) or v<=0 for v in (days,yield_,minimum)):continue
        if not finite(sensitivity) or sensitivity<0:continue
        season=climate.get('growingDaysRemaining',climate.get('growingDays'))
        if season is not None and (not finite(season) or days*2.5>season):continue
        fertility=[c.get('fertility',0) for c in facts.get('cells',[]) if c.get('walkable') is True
                   and not c.get('occupied') and not c.get('zone') and not c.get('roofed')
                   and finite(c.get('fertility')) and c['fertility']>=minimum]
        if not fertility:continue
        average=sum(fertility)/len(fertility)
        factor=max(0,1+(average-1)*sensitivity)
        choices.append((-yield_*factor/days,days,name))
    if not choices:return None
    runway=facts.get('foodRunwayDays')
    if finite(runway) and runway<min(c[1] for c in choices)*2.5:
        return min(choices,key=lambda c:(c[1],c[0],c[2]))[2]
    return min(choices)[2]


def field_target(facts, target_days=7, crop=None):
    rice = facts.get('definitions', {}).get(crop or facts.get('foodCrop') or 'Plant_Rice', {})
    values = (rice.get('nutritionDemandPerDay',facts.get('nutritionPerDay')), rice.get('growDays'),
              rice.get('harvestNutrition'), target_days)
    if any(isinstance(v, bool) or not isinstance(v, (int, float))
           or not isfinite(v) or v <= 0 for v in values):
        return None
    demand, grow_days, nutrition, target_days = values
    # The existing 2.5 growth allowance covers a planning cycle. A larger
    # player reserve requires enough yield to replenish that reserve per cycle.
    return max(facts.get('colonists', 0) * 10,
               ceil(demand * max(grow_days * 2.5, target_days) / nutrition))


def growing_cells(facts):
    return sum(f.get('growingCells', 0) for f in facts.get('farms', [])
               if f.get('edible') is True)


def field_coverage(facts,target_days=7,*,cells='growingCells'):
    """Sum fractions of the target supported by each observed crop's own yield."""
    coverage=0.
    for farm in facts.get('farms',[]):
        if farm.get('edible') is not True:continue
        crop=farm.get('crop')
        if not crop:return None
        required=field_target(facts,target_days,crop)
        if required is None:return None
        count=farm.get(cells)
        if type(count) not in (float,int) or not isfinite(count) or count<0:return None
        coverage+=count/required
    return coverage

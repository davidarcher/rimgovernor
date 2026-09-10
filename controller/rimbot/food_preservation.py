"""Preserve forecast-at-risk food through native recipes and ordinary bills."""
from math import ceil


def preservation_bill(facts,target_days):
    forecast=facts.get('foodForecast') or {}
    if not forecast.get('readable') or forecast.get('atRiskNutrition',0)<=0:return None
    if forecast.get('runwayDays',0)>=target_days:return None
    choices=[]
    for bench in facts.get('cooking',[]):
        if bench.get('usable') is not True:continue
        for recipe in bench.get('production',[]):
            if recipe.get('available') is not True:continue
            products=recipe.get('products',[])
            if len(products)!=1:continue
            product=products[0]
            nutrition=product.get('nutrition')
            if product.get('edible') is not True or not nutrition or nutrition<=0:continue
            shelf=product.get('rotDays')
            if shelf is not None and shelf<=target_days:continue
            quantity=ceil(facts['nutritionPerDay']*target_days/nutrition)
            covered=any(b.get('recipe')==recipe['recipe'] and b.get('suspended') is False
                and (b.get('repeatMode')=='Forever' or (b.get('repeatMode')=='TargetCount'
                     and b.get('targetCount',0)>=quantity)) for b in bench.get('bills',[]))
            if covered:return None
            choices.append((recipe['recipe'],bench['id'],quantity,product['defName']))
    if not choices:return None
    recipe,bench,quantity,product=min(choices)
    return dict(recipe=recipe,bench=bench,targetCount=quantity,product=product)

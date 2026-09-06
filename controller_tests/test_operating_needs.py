import json
from rimbot.decision_context import decision_context
from rimbot.semantic_models import ObjectiveProposal


def test_resource_projection_keeps_food_and_animals_beyond_first_three():
    supplies=[{'def_name':name,'quantity':50} for name in ['Silver','Steel','WoodLog','MealSurvivalPack']]
    animals=[{'def_name':name} for name in ['Hare','Rat','Iguana','Horse']]
    context={'resource_overview':{'supplies':{'items':supplies,'omitted_groups':0},'animals':{'items':animals,'omitted_groups':0}}}
    result=decision_context(context,{})['resource_overview']
    assert result['supplies']['items']==supplies
    assert result['animals']['items']==animals
    assert result['supplies']['omitted_groups']==0


async def test_pen_alert_brings_feeding_guidance_to_manager(colony):
    rt,_=colony
    rt.observation['alerts']=[{'label':'Pen needed','explanation':'Horse needs a pen'}]
    async def complete(messages,tools,*args):
        context=json.loads(messages[1]['content'])
        pen=next(e for e in context['strategy_guidance'] if e['id']=='pen-layout')
        assert pen['version']==2
        assert any('feeding' in text for text in pen['approach'])
        assert 'operational outcomes' in messages[0]['content']
        return {'role':'assistant','content':ObjectiveProposal(summary='Inspect feed before choosing a pen site').model_dump_json()},{}
    rt.model.complete=complete
    await rt.planner.ask('Infrastructure',{},ObjectiveProposal)


def test_grouped_supplies_count_all_food_and_native_material_categories():
    from rimbot.resources import summarize_supplies
    def row(name,count,category=(),food='',nutrition=0):
        return dict(def_name=name,quantity=count,allowed_quantity=0,forbidden_quantity=count,nearby_allowed_quantity=0,nearby_forbidden_quantity=count,material_categories=list(category),food_type=food,nutrition_per_unit=nutrition)
    rows=[row('FancyMetal',800,['Metallic']),row('OtherMetal',400,['Metallic']),row('TimberMod',300,['Woody']),row('Meals',50,food='Meal',nutrition=.9),row('Hay',100,food='Plant',nutrition=.05),row('RockChunk',100)]
    summary=summarize_supplies(rows)
    assert summary['construction_materials']['Metallic']['quantity']==1200
    assert summary['construction_materials']['Woody']['quantity']==300
    assert summary['nutrition']['Meal']['nearby_forbidden_quantity']==45
    assert summary['nutrition']['Plant']['quantity']==5
    assert sum(g['quantity'] for g in summary['construction_materials'].values())==1500
    result=decision_context({'resource_overview':{'supply_summary':summary,'supplies':{'items':rows,'omitted_groups':0}}},{})
    assert 'supplies' not in result['resource_overview']
    assert result['resource_overview']['supply_summary']==summary

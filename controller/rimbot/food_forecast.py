"""Observed stock trend, deliberately distinct from metabolic food runway."""
import math
from .food_access import runway


def forecast(memory,observation):
    summary=((observation.get('resources') or {}).get('critical_resources') or {}).get('food_summary')
    unknown={'stock_depletion_days':None,'unavailable':'No current native food summary'}
    if not isinstance(summary,dict):return unknown
    amount=summary.get('total_nutrition')
    if not isinstance(amount,(int,float)) or not math.isfinite(amount) or amount<0:return unknown
    game=observation.get('game',{});tick=game.get('game_tick',0);population=game.get('colonist_count')
    samples=memory.setdefault('food_samples',[])
    if samples and (tick<samples[-1]['tick'] or population!=samples[-1]['population'] or tick-samples[-1]['tick']>15000):
        samples.clear()
    if not samples or tick-samples[-1]['tick']>=2500:
        samples.append({'tick':tick,'nutrition':amount,'population':population})
    samples[:]=[s for s in samples[-25:] if tick-s['tick']<=60000]
    result={'total_nutrition':amount,'allowed_nutrition':summary.get('unforbidden_nutrition'),
        'forbidden_nutrition':summary.get('forbidden_nutrition'),'stock_depletion_days':None,
        'food_runway_days':None,
        'scope':'Stock totals include forbidden/unexplored stacks and exclude pawn inventories. Net change includes eating, hauling into inventories, spoilage, production and trade. Dietary coverage is reported separately in food_runway_days; do not use stock totals to choose items to allow.',
        'sample_count':len(samples)}
    result.update(runway(summary.get('access')))
    elapsed=tick-samples[0]['tick']
    if elapsed<15000 or len(samples)<4:
        result['unavailable']='Stock trend needs at least six game hours and four samples; dietary coverage is a separate measurement.'
        return result
    loss=(samples[0]['nutrition']-amount)/(elapsed/60000)
    result.update(window_days=round(elapsed/60000,3),net_loss_per_day=round(loss,3))
    if loss>0:result['stock_depletion_days']=round(amount/loss,2)
    else:result['trend']='No net stock decline in the observed window; this is not proof of adequate food.'
    return result

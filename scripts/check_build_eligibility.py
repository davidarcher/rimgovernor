"""Read-only regression against a loaded colony with a restricted building.

Example: python scripts/check_build_eligibility.py --restricted SlabBed --available Bed
Fails if discovery and placement disagree, or existing sites disappear.
"""
import argparse,json
import requests
p=argparse.ArgumentParser();p.add_argument('--url',default='http://127.0.0.1:8765');p.add_argument('--map-id',type=int,default=0);p.add_argument('--restricted',required=True);p.add_argument('--available',required=True);a=p.parse_args()
def post(route,body):
 r=requests.post(a.url+'/api/v2/construction/'+route,json=body,timeout=20);r.raise_for_status();return r.json()
def definition(name):
 return next(d for d in post('definitions',dict(map_id=a.map_id,search=name,offset=0,limit=32))['items'] if d['def_name']==name)
bad=definition(a.restricted);good=definition(a.available)
assert bad['eligibility']['restrictions'],bad
assert not good['eligibility']['restrictions'],good
assert good['eligibility']['eligible_pawn_ids'] or good['work_to_build']==0,good
state=post('state',{'map_id':a.map_id})
# Search a small ring around actual colony construction, not an arbitrary map corner.
origin=next(b['position'] for b in state['buildings'])
material=next((m['def_name'] for m in good['allowed_materials'] if m in bad['allowed_materials']), '')
found=False
for radius in range(1,12):
 for dx,dz in ((radius,0),(-radius,0),(0,radius),(0,-radius)):
  pos={'x':origin['x']+dx,'z':origin['z']+dz}
  placement=dict(def_name=a.available,stuff_def_name=material,position=pos,rotation=0)
  result=post('inspect',{'map_id':a.map_id,'buildings':[placement]})
  if not result['accepted'] or result['items'][0]['state']!='ready':continue
  placement['def_name']=a.restricted
  rejected=post('inspect',{'map_id':a.map_id,'buildings':[placement]})
  assert not rejected['accepted'],rejected
  assert any(reason in rejected['items'][0]['reason'] for reason in bad['eligibility']['restrictions']),rejected
  found=True;break
 if found:break
assert found,'No comparison site found near existing construction'
assert post('state',{'map_id':a.map_id})==state,'Read-only eligibility changed construction state'
print(json.dumps({'restricted':bad['def_name'],'reasons':bad['eligibility']['restrictions'],'available':good['def_name'],'eligible_pawns':good['eligibility']['eligible_pawn_ids'],'placement_rejected':rejected['items'][0]['reason'],'construction_unchanged':True}))

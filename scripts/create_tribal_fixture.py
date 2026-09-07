"""Create the eight-member Lost Tribe benchmark save through native new-game setup.

Requires an idle Manual controller. Replaces the loaded disposable game. Reuse
this save for benchmarks; world seed alone does not fix pawn generation.
"""
import argparse,hashlib,json,time,xml.etree.ElementTree as ET
from pathlib import Path
import requests

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--execute',action='store_true',required=True);p.add_argument('--name',default='RimBot-tribal8-baseline');p.add_argument('--seed',default='rimbot-tribal-eight');p.add_argument('--difficulty',default='Medium');a=p.parse_args()
 if not a.name or any(c in a.name for c in '/\\:'):raise ValueError('Use a plain save name')
 folder=Path.home()/'AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Saves';save=folder/(a.name+'.rws')
 if save.exists():raise ValueError('Baseline already exists; reload it rather than overwrite it. Choose --name for a new fixture.')
 controller=requests.get('http://127.0.0.1:8787/api/state').json()
 if controller['mode']!='manual' or controller['busy']:raise ValueError('Controller must be idle and Manual')
 base=controller['settings']['rimapi_url']
 def call(path,body=None,params=None):
  r=requests.post(base+path,json=body,params=params,timeout=120) if body is not None or params is not None else requests.get(base+path,timeout=15)
  r.raise_for_status();v=r.json()
  if not v.get('success',True):raise RuntimeError(v)
  return v.get('data',v)
 before=call('/api/v1/game/state').get('session_id')
 config=dict(scenario_def_name='LostTribe',starting_pawn_count=8,storyteller_name='Cassandra',difficulty_name=a.difficulty,map_size=250,permadeath=False,planet_coverage=.3,world_seed=a.seed,starting_tile='auto',starting_season='1',overall_rainfall=2,overall_temperature=2,overall_population=2,landmark_density=2)
 call('/api/v1/game/start',config)
 for _ in range(180):
  time.sleep(1);state=call('/api/v1/game/state')
  if state.get('program_state')=='Playing' and state.get('session_id')!=before:break
 else:raise TimeoutError('Tribal game did not finish loading')
 call('/api/v1/game/speed',params={'speed':0})
 assert state['colonist_count']==8,state
 call('/api/v1/game/save',{'file_name':a.name})
 for _ in range(60):
  try:root=ET.parse(save).getroot();break
  except (OSError,ET.ParseError):time.sleep(.5)
 else:raise RuntimeError('Save did not finish writing valid XML')
 assert root.tag=='savegame'
 assert root.findtext('game/scenario/playerFaction/factionDef')=='PlayerTribe'
 assert root.findtext("game/scenario/parts/li[@Class='ScenPart_ConfigPage_ConfigureStartingPawns']/pawnCount")=='8'
 report={'save':str(save),'sha256':hashlib.sha256(save.read_bytes()).hexdigest(),'configuration':config,'initial_state':state}
 output=Path('.rimbot/fixtures');output.mkdir(parents=True,exist_ok=True);(output/(a.name+'.json')).write_text(json.dumps(report,indent=2),encoding='utf-8')
 print(json.dumps(report),flush=True)
if __name__=='__main__':main()

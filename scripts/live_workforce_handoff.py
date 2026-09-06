"""Real-model workforce/arbitration test; restores the one work priority it changes."""
import argparse,asyncio,json,time
from pathlib import Path
import httpx
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.config import Settings
from rimbot.contracts import Proposal,Action

async def main():
    dashboard=httpx.get('http://127.0.0.1:8787/api/state').json()
    assert dashboard['mode']=='manual','Put the dashboard in Manual before this test.'
    folder=Path('.rimbot/workforce-tests')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings.model_validate(dashboard['settings']))
    report={'passed':False};original=None;original_mode=None;resume_for_test=False;started=time.monotonic()
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        original_mode=await rt.api.call('get_work_settings',{},fresh=True)
        for observed in rt.observation['pawns']:
            pid=observed['colonist']['id']
            pawn=await rt.api.call('get_colonist_detailed',{'id':pid},fresh=True)
            choices=[w for w in pawn['colonist_work_info']['work_priorities'] if not w['is_totally_disabled'] and w['priority']>0]
            if choices:
                selected=next((w for w in choices if w['work_type']=='Construction'),choices[0]);break
        else:raise AssertionError('No enabled work priority available for test.')
        work=selected['work_type'];old=selected['priority'];target=1 if old!=1 else 2
        original={'priorities':[{'id':pid,'work':work,'priority':old}]}
        direction=f'Set pawn {pid} work priority {work} to {target}. This is a work-tab configuration change, not a forced job. No other changes.'
        context={'player_direction':[direction],'assignments':{'Workforce':direction,'Security':'Assess defense sites; do not assign ordinary labor'},'plans':{'today':['Defense preparation','Configure work priority']}}
        proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Workforce']),120)
        report.update(proposals=proposals,decision=decision.model_dump())
        actions=[a for role in decision.accepted for a in Proposal.model_validate(proposals[role]).actions]
        assert actions,'Administrator accepted no work-priority action.'
        expected={'priorities':[{'id':pid,'work':work,'priority':target}]}
        assert all((a.endpoint=='post_work_settings' and a.arguments=={'use_work_priorities':True}) or (a.endpoint=='post_colonists_work_priority' and a.arguments==expected) or (a.endpoint=='post_colonist_work_priority' and a.arguments==expected['priorities'][0]) for a in actions),actions
        resume_for_test=(await rt.api.call('get_game_state',{},fresh=True))['is_paused']
        if resume_for_test:await rt.api.request('POST','/api/v1/game/speed',params={'speed':1})
        for action in actions:await rt.execute(action,'Workforce')
        current=await rt.api.call('get_colonist_detailed',{'id':pid},fresh=True)
        assert any(w['work_type']==work and w['priority']==target for w in current['colonist_work_info']['work_priorities'])
        assert rt.memory['work'][-1]['status']=='complete'
        report['passed']=True
    except BaseException as error:
        report['error']=repr(error);raise
    finally:
        if original is not None:
            await rt.api.call('post_colonists_work_priority',original,write=True)
            restored=await rt.api.call('get_colonist_detailed',{'id':pid},fresh=True)
            report['restored']=any(w['work_type']==work and w['priority']==old for w in restored['colonist_work_info']['work_priorities'])
        if original_mode is not None:
            await rt.api.call('post_work_settings',original_mode,write=True)
            report['mode_restored']=(await rt.api.call('get_work_settings',{},fresh=True))==original_mode
        if resume_for_test:await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        report.update(seconds=round(time.monotonic()-started,2),counters=rt.counters)
        (folder/'report.json').write_text(json.dumps(report,indent=2));print(report,flush=True)
        await rt.stop();rt.store.close()

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--execute',action='store_true',required=True);parser.parse_args();asyncio.run(main())

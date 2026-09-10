"""Native confirmation and final game-value acceptance using gated disposable fixtures."""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import json
import subprocess
from pathlib import Path

from rimgovernor.bridge import bridge_session, BridgeError
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.store import Store

NAMING=('faction','settlement','combined','zone','area','policy','bill','storage',
        'storage_new','pen','gravship','gravship_given','pawn','animal')


async def run(args):
    root=isolated_root(args.source,args.output)
    config_dir=prepare(root)
    config=json.loads((config_dir/'config.json').read_text())
    config['games']['rimgovernor-trial']['args']=[a for a in config['games']['rimgovernor-trial']['args']
                                           if a not in ('-batchmode','-nographics')]
    (config_dir/'config.json').write_text(json.dumps(config))
    report={'revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
            'cases':{},'outcome':'failed'}
    try:
        async with bridge_session(gabs_executable(root),config_dir) as bridge:
            try:
                await bridge.core('games_start',gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',
                                  readiness='visual',timeoutMs=90000,ignoreModCompatibility=True)
                await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                store=Store(root/'state.sqlite')
                rt=BridgeRuntime(store,root)
                rt.bridge,rt.game=bridge,BridgeGame(bridge)
                rt.headless=False
                try:
                    await rt.sync_identity()
                    rt.mode='automate'
                    rt.connected=True
                    async def fixture(**kw):
                        value=(await bridge.call('test/modal_fixture',**kw)).structuredContent
                        assert value.get('success') is True,value
                        return value
                    async def capture():
                        layout=await rt.inspect_native('rimworld/get_ui_layout',{'timeoutMs':10000})
                        controls=[e for s in layout.get('surfaces',[]) for e in s.get('elements',[])
                                  if e.get('actionable') and e.get('disabled') is not True]
                        return layout,controls
                    async def click(button):
                        return await rt.native('rimworld/click_ui_target',{'targetId':button['targetId']},reconcile=False)
                    for kind in args.cases:
                        row={};report['cases'][kind]=row
                        if kind=='race':
                            await rt.supervisor.change('Normal')
                            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                            await asyncio.sleep(.3)
                            rt.clock_events.extend(await rt.supervisor.poll())
                            rt.receive_clock_events()
                            row['external_hold']=rt.supervisor.hold
                            assert rt.mode=='manual' and rt.supervisor.hold,row
                        row['before']=await fixture(action='open',kind='zone' if kind=='race' else kind)
                        if kind in NAMING:
                            fields=await rt.game.invoke('home/dialog_text',{'list':True,'dryRun':True})
                            row['fields']=fields
                            assert fields['writable'] is True,fields
                            field='curName'
                            if kind in ('pawn','animal'):
                                namefields=[f['name'] for f in fields['fields'] if f['name'].startswith('names[')]
                                assert len(namefields)==1,fields
                                field=namefields[0]
                            text='Fixture '+kind.replace('_',' ')
                            if kind in ('pawn','animal'):text='FixtureName'
                            arguments={'windowId':fields['windowId'],'field':field,'text':text,'dryRun':False}
                            limit=next(f['maxLength'] for f in fields['fields'] if f['name']==field)
                            assert isinstance(limit,int),fields
                            try:
                                await rt.game.invoke('home/dialog_text',dict(arguments,text='X'*(limit+1)),allow_write=True)
                            except (ValueError,BridgeError) as error:row['length_refused']=str(error)
                            else:raise AssertionError('Native input length was bypassed')
                            try:
                                await rt.game.invoke('home/dialog_text',dict(arguments,accept=True),allow_write=True)
                            except (ValueError,BridgeError) as error:row['shortcut_refused']=str(error)
                            else:raise AssertionError('Accept shortcut was not refused')
                            row['field_receipt']=await rt.native('home/dialog_text',arguments,reconcile=False)
                            if kind=='combined':
                                row['second_receipt']=await rt.native('home/dialog_text',dict(arguments,
                                    field='curSecondName',text='Fixture second'),reconcile=False)
                            row['typed_uncommitted']=await fixture()
                            assert row['typed_uncommitted']['value']==row['before']['value'],row
                            layout,controls=await capture();row['layout']=layout
                            buttons=[b for b in controls if b.get('label') in ('OK','Accept')]
                            assert len(buttons)==1,(kind,controls)
                            row['confirmation']=await click(buttons[0])
                            row['after']=await fixture()
                            expected={'first':text,'second':'Fixture second'} if kind=='combined' else text
                            assert row['after']['value']==expected,row
                            assert row['after']['windowOpen'] is False and row['after']['paused'] is True,row
                        elif kind=='quest':
                            layout,controls=await capture();row['layout']=layout
                            buttons=[b for b in controls if (b.get('label') or '').startswith('Accept for')]
                            assert len(buttons)==2,controls
                            row['confirmation']=await click(buttons[1])
                            row['after']=await fixture()
                            value=row['after']['value']
                            assert value['state']=='Ongoing' and value['choiceCount']==1,value
                            assert value['rewards']==[22] and value['branchesPresent']==[False,True],value
                            state=await rt.game.invoke('rimworld/get_ui_state',{})
                            row['tab_closed']=await rt.native('rimworld/close_main_tab',
                                {'mainTabId':state['openMainTabId']},reconcile=False)
                        elif kind=='race':
                            try:
                                await rt.native('rimworld/open_letter',{'letterId':'unselected'})
                            except (ValueError,BridgeError) as error:row['held_open_refused']=str(error)
                            else:raise AssertionError('External hold was bypassed')
                            assert rt.supervisor.hold==row['external_hold'] and rt.mode=='manual',row
                            # Simulate explicit player Enable Automate, not an automatic hold release.
                            await rt.set_mode('automate')
                            try:
                                await rt.native('rimworld/open_letter',{'letterId':'unselected'})
                            except (ValueError,BridgeError) as error:row['existing_open_refused']=str(error)
                            else:raise AssertionError('Existing window was bypassed')
                            fields=await rt.game.invoke('home/dialog_text',{'list':True,'dryRun':True})
                            layout,controls=await capture();row['layout']=layout
                            button=next(b for b in controls if b.get('label')=='OK')
                            row['replacement']=await fixture(action='replace')
                            for name,arguments in (
                                ('home/dialog_text',{'windowId':fields['windowId'],'field':'curName','text':'Stale','dryRun':False}),
                                ('rimworld/click_ui_target',{'targetId':button['targetId']})):
                                try:await rt.native(name,arguments,reconcile=False)
                                except (ValueError,BridgeError) as error:row[name+'_refused']=str(error)
                                else:raise AssertionError('Stale modal action accepted: '+name)
                            row['replacement_intact']=await fixture()
                            assert row['replacement_intact']['windowOpen'] and row['replacement_intact']['paused'],row
                            targets=await rt.game.invoke('rimworld/get_screen_targets',{})
                            target=next(w for w in targets['targets']['windows'] if w['id']==row['replacement']['windowId'])
                            row['recovery']=await rt.native('rimworld/click_screen_target',
                                {'targetId':target['dismissTargetId']},reconcile=False)
                            row['after']=await fixture()
                            assert not row['after']['windowOpen'] and row['after']['paused'],row
                            assert not rt.resume_after_review,row
                    report['outcome']='passed'
                finally:
                    await rt.halt()
                    await rt.router.close()
                    store.close()
            finally:
                await bridge.core('games_kill',gameId=bridge.game_id)
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        (root/'modal-result.json').write_text(json.dumps(report,indent=2),encoding='utf8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=Path('.rimgovernor/bridge'))
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--cases',nargs='+',default=[*NAMING,'quest','race'])
    asyncio.run(run(parser.parse_args()))

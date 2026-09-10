"""B13 native settings and capability audit in a disposable rendered Docker worker.

Run through rimgovernor.container_worker with this script as its command. Inputs are
copied by that runner. No model calls, debug actions, or edited saves are used.
"""
import asyncio
import json
import os
from pathlib import Path
from rimgovernor.bridge import bridge_session, gabs_executable, BridgeError
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge_observation import observe
from rimgovernor.player_commands import apply_command
from rimgovernor.store import Store


async def run():
    root=Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    report={'passed':False,'scope':'Native player settings, zone geometry, bill whitelist and capability audit; no downstream pawn labor assertion.'}
    config=root/'config'
    async with bridge_session(gabs_executable(root,config),config) as bridge:
        rt=None
        try:
            await bridge.core('games_start',gameId=bridge.game_id)
            await bridge.connect()
            await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=120000)
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            game=BridgeGame(bridge)
            rt=BridgeRuntime(Store(root/'acceptance.sqlite'),root,headless=False)
            rt.bridge=bridge;rt.game=game
            await rt.sync_identity();rt.batch=await observe(game);rt.mode='automate'
            report['identity']=rt.context_token
            report['catalog']={}
            for query in ('context','gizmo','dropdown','designator','queue','selection','pawn','policy','surgery'):
                report['catalog'][query]=(await bridge.names(query=query)).structuredContent
            report['schemas']={}
            for tool in ('home/zone_cells','home/bills','home/pawn_config','home/order',
                         'rimworld/list_selected_gizmos','rimworld/get_selection_semantics','rimworld/click_ui_target'):
                report['schemas'][tool]=(await bridge.detail(tool)).structuredContent
            for catalog in report['catalog'].values():
                assert not catalog.get('nextCursor'), 'Audit needs another catalog page'
                for row in catalog.get('tools',[]):
                    name=row['gabpName']
                    if name not in report['schemas']:
                        report['schemas'][name]=(await bridge.detail(name)).structuredContent
            (root/'player-actions.json').write_text(json.dumps(report,indent=2),encoding='utf8')
            report['cases']={}
            async def native(case,tool,**arguments):
                print(case,flush=True)
                value=await rt.native(tool,arguments,reconcile=False,expected_revision=rt.chat_revision,expected_token=rt.context_token)
                report['cases'][case]=value
                (root/'player-actions.json').write_text(json.dumps(report,indent=2),encoding='utf8')
                return value['receipt']
            async def command(label,**request):
                accepted=await apply_command(rt,request,token=rt.context_token,revision=rt.chat_revision)
                step=next(s for s in rt.current_plan.spec.steps if s.id==accepted['step'])
                value=await native(label,step.action.tool,**step.action.arguments)
                report['cases'][label]['accepted']=accepted
                return value
            pawns=await game.query('home/list_pawns',colonistsOnly=True)
            await bridge.call('rimworld/select_pawn',pawnId=pawns['pawns'][0]['thingId'],append=False)
            report['cases']['gizmos']=(await bridge.call('rimworld/list_selected_gizmos')).structuredContent
            report['cases']['selection']=(await bridge.call('rimworld/get_selection_semantics')).structuredContent
            draft_gizmo=next(g for g in report['cases']['gizmos']['gizmos'] if g.get('hotKeyDef')=='Command_ColonistDraft')
            await bridge.call('rimworld/select_pawn',pawnId=pawns['pawns'][1]['thingId'],append=False)
            try:
                stale=(await bridge.call('rimworld/execute_gizmo',gizmoId=draft_gizmo['id'])).structuredContent
                assert stale.get('success') is False, stale
            except BridgeError as error:
                stale={'refused':str(error)}
            report['cases']['stale_gizmo']=stale
            after=await game.query('home/list_pawns',colonistsOnly=True)
            assert [p['drafted'] for p in after['pawns']]==[p['drafted'] for p in pawns['pawns']]
            await bridge.call('rimworld/select_pawn',pawnId=pawns['pawns'][0]['thingId'],append=False)
            for drafted in (True,False):
                listing=(await bridge.call('rimworld/list_selected_gizmos')).structuredContent
                gizmo=next(g for g in listing['gizmos'] if g.get('hotKeyDef')=='Command_ColonistDraft')
                report['cases']['gizmo_'+str(drafted)]=(await bridge.call('rimworld/execute_gizmo',gizmoId=gizmo['id'])).structuredContent
                actual=await game.query('home/list_pawns',colonistsOnly=True)
                assert next(p for p in actual['pawns'] if p['thingId']==pawns['pawns'][0]['thingId'])['drafted'] is drafted
                report['cases']['gizmo_readback_'+str(drafted)]=actual
            report['cases']['main_tabs']=(await bridge.call('rimworld/list_main_tabs')).structuredContent
            await native('open_work','rimworld/open_main_tab',mainTabId='Work')
            report['cases']['work_layout']=await rt.inspect_native('rimworld/get_ui_layout',{})
            layout=report['cases']['work_layout']
            for index in range(2):
                toggle=next(e for s in layout['surfaces'] for e in s['elements']
                    if e.get('kind')=='checkbox' and e.get('label')=='Manual priorities')
                await native('work_toggle_'+str(index),'rimworld/click_ui_target',targetId=toggle['targetId'])
                actual=await game.query('home/list_pawns',colonistsOnly=True,work=True)
                assert all(p['work']['manualPriorities'] is (not toggle['isChecked']) for p in actual['pawns'])
                report['cases']['work_readback_'+str(index)]=actual
                try:
                    await native('stale_ui','rimworld/click_ui_target',targetId=toggle['targetId'])
                    raise AssertionError('A consumed UI target was reused')
                except ValueError as error:
                    report['cases']['consumed_ui_refusal']=str(error)
                layout=await rt.inspect_native('rimworld/get_ui_layout',{})
            await native('close_work','rimworld/close_main_tab',mainTabId='Work')
            await bridge.call('rimworld/clear_selection')
            anchor=pawns['pawns'][0]['position']
            created=None
            for dx,dz in ((6,6),(-8,6),(6,-8),(-8,-8),(12,0),(0,12)):
                x,z=anchor['x']+dx,anchor['z']+dz
                args=dict(op='create',zoneType='growing',label='B13 growing',plant='Plant_Potato',x=x,z=z,width=2,height=2,watch=False)
                preview=await game.invoke('home/zone_cells',dict(args,dryRun=True))
                report['cases'][f'site_{dx}_{dz}']=preview
                if preview.get('success') and len(preview.get('cells',[]))==4 and all(c.get('accepted') for c in preview['cells']):
                    created=await native('create_growing','home/zone_cells',**args,dryRun=False)
                    break
            assert created,'No legal growing-zone fixture site'
            zone=created['zone']['id']
            await command('crop',kind='EditZone',zone_id=zone,operation='crop',crop='Plant_Rice')
            await command('expand',kind='EditZone',zone_id=zone,operation='add',cells=[dict(x=x+2,z=z)])
            await command('remove',kind='EditZone',zone_id=zone,operation='remove',cells=[dict(x=x+2,z=z)])
            await command('delete_growing',kind='EditZone',zone_id=zone,operation='delete')
            stock=await native('create_stockpile','home/zone_cells',op='create',zoneType='stockpile',label='B13 stockpile',x=x,z=z,width=2,height=2,dryRun=False,watch=False)
            zone=stock['zone']['id']
            changed=await command('special_filter',kind='EditZone',zone_id=zone,operation='filter',preset='everything',disallow=['special:AllowRotten'],priority='Critical')
            assert changed['changed'] is True
            assert next(f for f in changed['after']['specialFilters'] if f['defName']=='AllowRotten')['allowed'] is False
            before=await game.query('home/list_zones',filter=True)
            try:
                invalid=await game.invoke('home/zone_cells',dict(op='filter',zone=str(zone),allow='special:NotAFilter',dryRun=False,watch=False),allow_write=True)
                assert invalid.get('success') is False
            except BridgeError as error:
                invalid={'refused':str(error)}
            after=await game.query('home/list_zones',filter=True)
            assert after['zones']==before['zones']
            report['cases']['invalid_filter']=invalid
            await command('special_filter_restore',kind='EditZone',zone_id=zone,operation='filter',allow=['special:AllowRotten'])
            await command('delete_stockpile',kind='EditZone',zone_id=zone,operation='delete')
            placed=await native('crafting_spot','home/place_building',defName='CraftingSpot',x=x,z=z,rotation='north',dryRun=False,watch=False)
            assert placed.get('success') is True
            benches=await game.invoke('home/bills',{'action':'list','dryRun':True})
            report['cases']['benches']=benches
            recipes=await game.invoke('home/bills',{'action':'recipes','bench':'CraftingSpot','dryRun':True})
            report['cases']['recipes']=recipes
            recipe=next(row for row in recipes['recipes'] if 'WoodLog' in row.get('filter',{}).get('allowedDefNames',[])
                        and len(row['filter']['allowedDefNames'])>1)
            report['cases']['selected_recipe']=recipe
            bill=await command('bill_whitelist',kind='CreateBill',bench='CraftingSpot',recipe=recipe['defName'],target_count=1,ingredients=['WoodLog'])
            assert bill.get('success') is True
            report['cases']['bill_readback']=await game.invoke('home/bills',{'action':'list','bench':'CraftingSpot','dryRun':True})
            rows=[b for bench in report['cases']['bill_readback']['benches'] for b in bench.get('bills',[])]
            assert any(row.get('filter',{}).get('allowedDefNames')==['WoodLog'] for row in rows), rows
            report['passed']=True
        except Exception as error:
            report['error']=repr(error)
            raise
        finally:
            (root/'player-actions.json').write_text(json.dumps(report,indent=2),encoding='utf8')
            if rt: rt.store.close()
            await bridge.core('games_stop',gameId=bridge.game_id)


if __name__=='__main__': asyncio.run(run())

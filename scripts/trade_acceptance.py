"""Real map buy/sell acceptance in a fresh staged Docker worker with TradeFixture."""
import asyncio
import json
from pathlib import Path

from rimbot.bridge import bridge_session, gabs_executable, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.colony_plan import TradeAction
from rimbot.trading import execute_trade


async def run():
    root = Path('/worker/run')
    report = {'passed': False, 'cases': []}
    def record(name, **data):
        report['cases'].append({'name': name, **data})
        (root/'trade-progress.json').write_text(json.dumps(report, indent=2))
        print(name, flush=True)

    async with bridge_session(gabs_executable(root), root/'config-headless') as bridge:
        game = BridgeGame(bridge)
        async def call(tool, **args):
            result = dict((await bridge.call(tool, **args)).structuredContent)
            result.pop('operation', None)
            return result
        async def trade(**args):
            return await game.invoke('home/trade', dict(watch=False, **args), allow_write=True)
        async def refused(*, expected, **args):
            try:
                result = await trade(**args)
            except BridgeError as error:
                message = str(error)
                assert any(fragment.lower() in message.lower() for fragment in expected), message
                return {'message': message, 'native': error.result.structuredContent}
            assert result.get('success') is False, result
            assert any(fragment.lower() in str(result).lower() for fragment in expected), result
            return result
        async def snapshot():
            return await call('test/trade_fixture', traderId=trader_id, pawnId=pawn_id)
        async def window(ticks=600):
            state = await call('home/supervised_play', op='start', owner='b10-fixture', maxTicks=ticks,
                               speed='Superfast', leaseMs=30000)
            async with asyncio.timeout(60):
                while state.get('active'):
                    await asyncio.sleep(.2)
                    state = await call('home/supervised_play', op='status')
            assert state.get('pauseVerified'), state
            assert state.get('stopReason') in ('tick_budget', 'force_paused', 'letter_pause'), state
            return state
        def total(state, side, definition):
            return sum(row['count'] for row in state[side] if row['defName'] == definition)

        try:
            await bridge.core('games_start', gameId=bridge.game_id)
            await bridge.connect()
            await call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual',
                       ignoreModCompatibility=True, timeoutMs=90000)
            await call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            record('ordinary_supply_pod_landing', clock=await window())
            setup = await call('test/trade_fixture')
            center = setup['colonists'][0]
            stock_cells = sorted({(t['x'], t['z']) for t in setup['ground']
                                  if not t['defName'].startswith('Chunk')
                                  and max(abs(t['x']-center['x']), abs(t['z']-center['z'])) < 35})
            stock_cells = [(x,z) for x in range(min(x for x,z in stock_cells), max(x for x,z in stock_cells)+1)
                           for z in range(min(z for x,z in stock_cells), max(z for x,z in stock_cells)+1)]
            zone_args = dict(op='create', zoneType='stockpile', label='Trade acceptance stock',
                             cells=';'.join(f'{x},{z}' for x,z in stock_cells))
            zone_preview = await game.invoke('home/zone_cells', dict(zone_args, dryRun=True))
            record('stockpile_preview', preview=zone_preview, stock=setup)
            stock_cells = [(row['x'], row['z']) for row in zone_preview['cells'] if row['accepted'] and not row['takenFrom']]
            assert stock_cells, 'No legal stockpile cells among ordinary starting goods'
            silver = next(t for t in setup['ground'] if t['defName'] == 'Silver')
            available = set(stock_cells)
            connected = {(silver['x'], silver['z'])}
            frontier = list(connected)
            while frontier:
                x,z = frontier.pop()
                for cell in ((x-1,z),(x+1,z),(x,z-1),(x,z+1)):
                    if cell in available and cell not in connected:
                        connected.add(cell)
                        frontier.append(cell)
            assert connected <= available
            wood_sources = [t for t in setup['ground'] if t['defName'] == 'WoodLog' and t['count'] > 0]
            assert wood_sources, 'Fixture has no ordinary wood supply for export hauling'
            wood = wood_sources[0]
            stock_cells = sorted(connected - {(wood['x'], wood['z'])})
            zone_args['cells'] = ';'.join(f'{x},{z}' for x,z in stock_cells)
            record('ordinary_stockpile', result=await game.invoke('home/zone_cells', dict(zone_args, dryRun=False), allow_write=True))
            # Make real surplus trade-eligible through ordinary hauling, without
            # replacing the food/medicine/equipment export protections.
            designators = await game.invoke('rimworld/list_architect_designators', {'categoryId': 'Orders'})
            allow = next(d['id'] for d in designators['designators'] if d['className'] == 'RimWorld.Designator_Unforbid')
            record('allow_export_supply', result=await game.invoke('rimworld/apply_architect_designator',
                {'designatorId': allow, 'x': wood['x'], 'z': wood['z'], 'dryRun': False, 'keepSelected': False}, allow_write=True))
            roster = await game.query('home/list_pawns', colonistsOnly=True, work=True)
            for worker in roster['pawns']:
                if not any(w['name'] == 'Hauling' and w.get('disabled') is False for w in worker['work']['types']):
                    continue
                haul = dict(action='haul', pawn=worker['thingId'], target=wood['id'], watch=False)
                preview_haul = await game.invoke('home/order', dict(haul, dryRun=True))
                if preview_haul.get('success') is True:
                    break
            else:
                raise AssertionError('No native hauling job can deliver export supply')
            before_haul = await call('test/trade_fixture')
            def stored_wood(state):
                return sum(t['count'] for t in state['ground'] if t['defName'] == 'WoodLog'
                           and (t['x'], t['z']) in set(stock_cells))
            record('ordinary_export_haul', receipt=await game.invoke('home/order', dict(haul, dryRun=False), allow_write=True))
            for _ in range(60):
                await window()
                after_haul = await call('test/trade_fixture')
                if stored_wood(after_haul) - stored_wood(before_haul) >= wood['count']:
                    break
            else:
                raise AssertionError('Export supply never reached native storage')
            assert total(after_haul, 'ground', 'WoodLog') == total(before_haul, 'ground', 'WoodLog')
            record('export_supply_delivered', before=before_haul, after=after_haul,
                   stored_before=stored_wood(before_haul), stored_after=stored_wood(after_haul))
            previous_wood = {t['id']: t for t in before_haul['ground'] if t['defName'] == 'WoodLog'}
            delivered = [t for t in after_haul['ground'] if t['defName'] == 'WoodLog'
                and (t['x'], t['z']) in set(stock_cells)
                and (t['id'] not in previous_wood or t['count'] > previous_wood[t['id']]['count']
                     or (t['x'], t['z']) != (previous_wood[t['id']]['x'], previous_wood[t['id']]['z']))]
            assert delivered, 'No exact native delivery stacks observed'
            for stack in delivered:
                if stack['forbidden']:
                    record('allow_merged_export_delivery', stack=stack,
                        result=await game.invoke('rimworld/apply_architect_designator',
                            {'designatorId': allow, 'x': stack['x'], 'z': stack['z'],
                             'dryRun': False, 'keepSelected': False}, allow_write=True))
            record('initial_discovery', discovery=await trade(action='list_traders'))
            record('ordinary_caravan_incident', incident=await call('test/trade_fixture', action='incident'))
            discovery = await trade(action='list_traders')
            (root/'discovery.json').write_text(json.dumps(discovery, indent=2))
            trader_id = next(t['id'] for t in discovery['traders'] if t['canTradeNow'])
            pawn_id = discovery['negotiator']['id']
            record('ordinary_trade_job', job=await call('test/trade_fixture', action='trade_job', traderId=trader_id, pawnId=pawn_id))
            for _ in range(100):
                state = await snapshot()
                if state['dialogOpen']:
                    break
                clock = await window()
            else:
                raise AssertionError('Negotiator never reached the trader through the ordinary job')
            assert state['negotiator'] == pawn_id, state
            record('native_job_arrival', snapshot=state, clock=clock)
            await trade(action='close_dialog', receiveQuest=False)
            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            session = opened['sessionId']
            record('stale_session_refused', refusal=await refused(expected=['session changed'], action='set', sessionId='stale', item='WoodLog', count=1))
            (root/'sheet.json').write_text(json.dumps(opened, indent=2))
            costly = next(row for row in opened['rows'] if row['traderCount'] > 0 and row['traderWillTrade']
                          and not row['isCurrency'] and not row['isPawn']
                          and row['buyPrice'] * row['traderCount'] > opened['balance']['colonySilverNow'] + 1
                          and sum(r['defName'] == row['defName'] for r in opened['rows']) == 1)
            before = await snapshot()
            await trade(action='set', sessionId=session, item=costly['defName'], count=costly['traderCount'])
            preview = await trade(action='preview')
            assert preview['balance']['colonyCanAfford'] is False
            record('colony_affordability_refused', preview=preview,
                   refusal=await refused(expected=['colony cannot afford'], action='accept', sessionId=session, dealSignature=preview['dealSignature']))
            assert await snapshot() == before
            await trade(action='cancel', sessionId=session, receiveQuest=False)
            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            session = opened['sessionId']
            full_sheet = await trade(action='sheet', includeUntradeable=True)
            record('native_export_demand', sheet=full_sheet)
            seed = next(row for row in sorted(opened['rows'], key=lambda r: r['defName'] != 'Steel')
                if row['traderCount'] >= 3 and row['traderWillTrade'] and row.get('protectedExport') is False
                and not row['isCurrency'] and not row['isPawn'] and row['sellPrice'] > 0
                and 0 < row['buyPrice'] * 3 <= opened['balance']['colonySilverNow']
                and sum(r['defName'] == row['defName'] for r in opened['rows']) == 1)
            await trade(action='cancel', sessionId=session, receiveQuest=False)

            async def exchange(item, count, lose_receipt=False):
                sheet = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
                row = next(r for r in sheet['rows'] if r['defName'] == item)
                await trade(action='cancel', sessionId=sheet['sessionId'], receiveQuest=False)
                before = await snapshot()
                writes = []
                accepted = None
                async def read(args): return await trade(**args)
                async def write(args):
                    nonlocal accepted
                    writes.append(args)
                    result = await trade(**args)
                    if args['action'] == 'accept':
                        accepted = result
                        if lose_receipt:
                            raise RuntimeError('Injected lost acceptance receipt after native execution')
                    return result
                action = TradeAction(trader_id=trader_id, negotiator=pawn_id,
                    policy={'silver_reserve': 0, 'targets': [{'item': item,
                        'stock': row['colonyCount'] + count,
                        'max_buy': max(0, count), 'max_sell': max(0, -count),
                        'max_buy_price': row['buyPrice'], 'min_sell_price': row['sellPrice']}]},
                    max_silver_spend=1000)
                try:
                    result = await execute_trade(action, read, write)
                except RuntimeError:
                    assert lose_receipt and accepted is not None
                    result = {'uncertain': True}
                after = await snapshot()
                record('exchange_readback', item=item, count=count, before=before, after=after, native=accepted, writes=writes)
                net = accepted['balanceBefore']['netSilverToColony']
                assert after['tick'] == before['tick']
                assert total(after, 'ground', item)-total(before, 'ground', item) == count
                assert total(after, 'goods', item)-total(before, 'goods', item) == -count
                assert total(after, 'ground', 'Silver')-total(before, 'ground', 'Silver') == net
                assert total(after, 'goods', 'Silver')-total(before, 'goods', 'Silver') == -net
                assert sum(w['action'] == 'accept' for w in writes) == 1
                old_counts = {row['id']: row['count'] for row in before['ground']}
                delivery = [row for row in after['ground'] if row['defName'] == item
                            and row['count'] > old_counts.get(row['id'], 0)] if count > 0 else []
                if count > 0:
                    assert delivery and all(row['spawned'] and not row['forbidden'] and row['traderProtected'] for row in delivery)
                record('lost_receipt_exchange' if lose_receipt else 'exchange', item=item, count=count,
                       before=before, after=after, native=accepted, result=result, writes=writes, delivery=delivery,
                       policy=action.model_dump())
                return after, writes[-1], delivery

            # Acquire the fixture commodity through an actual affordable purchase.
            # Its native delivery, not anticipated production, supplies the later export.
            _, _, seed_delivery = await exchange(seed['defName'], 3)
            seed_cells = sorted({(r['x'], r['z']) for r in seed_delivery} - set(stock_cells))
            seed_zone_args = None
            if seed_cells:
                seed_zone_args = dict(op='create', zoneType='stockpile', label='Delivered trade surplus',
                    cells=';'.join(f'{x},{z}' for x,z in seed_cells))
                record('ordinary_delivery_stockpile', result=await game.invoke('home/zone_cells',
                    dict(seed_zone_args, dryRun=False), allow_write=True))
            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            sold = next(row for row in opened['rows'] if row['defName'] == seed['defName']
                        and row['colonyCount'] >= 3 and row['traderWillTrade'])
            await trade(action='cancel', sessionId=opened['sessionId'], receiveQuest=False)

            async def policy_no_exchange(target, *, floors=None):
                before = await snapshot()
                writes = []
                async def read(args): return await trade(**args)
                async def write(args):
                    writes.append(args)
                    return await trade(**args)
                action = TradeAction(trader_id=trader_id, negotiator=pawn_id,
                    policy={'silver_reserve': 0, 'targets': [target]}, max_silver_spend=1000)
                result = await execute_trade(action, read, write, floors=floors)
                assert result['moved'] == [] and all(w['action'] != 'accept' for w in writes)
                assert await snapshot() == before
                record('economic_policy_refusal', policy=action.model_dump(), result=result, writes=writes)

            protected = next(row for row in opened['rows'] if row['colonyCount'] > 0
                and row.get('protectedExport') is True and row['traderWillTrade']
                and not row['isPawn'] and not row['isCurrency']
                and sum(r['defName'] == row['defName'] for r in opened['rows']) == 1)
            await policy_no_exchange({'item': protected['defName'], 'stock': 0, 'max_sell': 100})
            await policy_no_exchange({'item': sold['defName'], 'stock': 0, 'max_sell': 100},
                                    floors={sold['defName']: sold['colonyCount']})
            for row, floor, expected_error in [(sold, sold['colonyCount'], 'reserve'), (protected, 0, 'protected')]:
                opened_policy = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
                await trade(action='set', sessionId=opened_policy['sessionId'], item=row['defName'], count=-1)
                preview_policy = await trade(action='preview')
                before_policy = await snapshot()
                record('atomic_economic_refusal', refusal=await refused(expected=[expected_error], action='accept',
                    sessionId=opened_policy['sessionId'], dealSignature=preview_policy['dealSignature'],
                    economicFloors=f"Silver=0;{row['defName']}={floor}"))
                assert await snapshot() == before_policy
                await trade(action='cancel', sessionId=opened_policy['sessionId'], receiveQuest=False)

            await policy_no_exchange({'item': 'WoodLog', 'stock': 0, 'max_sell': 100})
            await exchange(sold['defName'], -2)

            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            session = opened['sessionId']
            purchase_rows = sorted(opened['rows'], key=lambda row: (row['defName'] != 'Steel', row['defName'] != 'WoodLog'))
            bought = next(row for row in purchase_rows if row['traderCount'] > 0 and row['traderWillTrade']
                          and not row['isCurrency'] and not row['isPawn'] and row['buyPrice'] > 0
                          and row['buyPrice'] <= opened['balance']['colonySilverNow']
                          and sum(r['defName'] == row['defName'] for r in opened['rows']) == 1)
            await trade(action='set', sessionId=session, item=bought['defName'], count=1)
            preview = await trade(action='preview')
            await trade(action='set', sessionId=session, item=bought['defName'], count=0)
            record('stale_preview_refused', refusal=await refused(expected=['preview changed'], action='accept', sessionId=session, dealSignature=preview['dealSignature']))
            await trade(action='cancel', sessionId=session, receiveQuest=False)
            after, accepted_args, delivery = await exchange(bought['defName'], 1, lose_receipt=True)
            record('accepted_session_cannot_replay', refusal=await refused(expected=['No TradeSession'], **accepted_args))
            assert await snapshot() == after
            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            session = opened['sessionId']
            await trade(action='set', sessionId=session, item=bought['defName'], count=1)
            preview = await trade(action='preview')
            before = await snapshot()
            await game.invoke('home/zone_cells', dict(op='delete', zone=zone_args['label'], dryRun=False), allow_write=True)
            if seed_zone_args:
                await game.invoke('home/zone_cells', dict(op='delete', zone=seed_zone_args['label'], dryRun=False), allow_write=True)
            home = await call('test/trade_fixture', action='clear_trade_home', traderId=trader_id, pawnId=pawn_id)
            record('ordinary_stock_eligibility_edit', home=home)
            assert home['silverStillEligible'] is False
            record('stale_stock_eligibility_refused', refusal=await refused(expected=['stock changed'],
                action='accept', sessionId=session, dealSignature=preview['dealSignature']))
            assert await snapshot() == before
            await game.invoke('home/zone_cells', dict(zone_args, dryRun=False), allow_write=True)
            if seed_zone_args:
                await game.invoke('home/zone_cells', dict(seed_zone_args, dryRun=False), allow_write=True)
            record('ordinary_dismiss_job', job=await call('test/trade_fixture', action='dismiss_job', traderId=trader_id, pawnId=pawn_id))
            for _ in range(100):
                await window()
                departed = await snapshot()
                if departed['traderDismissed'] or not departed['traderPresent']:
                    break
            else:
                raise AssertionError('Ordinary dismissal job did not complete')
            record('dismissed_trader_refused', snapshot=departed, refusal=await refused(expected=['departed'], action='accept', sessionId=session,
                                                                                       dealSignature=preview['dealSignature']))
            for _ in range(100):
                if not departed['traderPresent']:
                    break
                await window()
                departed = await snapshot()
            assert not departed['traderPresent'], 'Trader never left the map'
            record('trader_departure', snapshot=departed,
                   refusal=await refused(expected=['departed'], action='accept', sessionId=session, dealSignature=preview['dealSignature']))
            for _ in range(100):
                released = [row for row in departed['ground'] if row['id'] in {r['id'] for r in delivery}]
                if len(released) == len(delivery) and all(not row['forbidden'] for row in released):
                    break
                await window()
                departed = await snapshot()
            assert total(departed, 'ground', bought['defName']) >= total(after, 'ground', bought['defName']), 'Purchase disappeared'
            assert len(released) == len(delivery) and all(not row['forbidden'] for row in released)
            record('purchase_retained_after_departure', delivery=released)
            await trade(action='close_dialog', receiveQuest=False)
            # Ordinary small visitors have a lower silver budget than caravans.
            for _ in range(3):
                record('ordinary_visitor_incident', incident=await call('test/trade_fixture', action='visitor_incident'))
                discovery = await trade(action='list_traders')
                visitors = [t for t in discovery['traders'] if t['canTradeNow']]
                if visitors:
                    break
            assert visitors, 'No trader generated by the bounded ordinary visitor fixture'
            trader_id = visitors[0]['id']
            await call('test/trade_fixture', action='trade_job', traderId=trader_id, pawnId=pawn_id)
            for _ in range(100):
                state = await snapshot()
                if state['dialogOpen']:
                    break
                await window()
            assert state['dialogOpen'], 'Negotiator did not reach the visitor'
            await trade(action='close_dialog', receiveQuest=False)
            opened = await trade(action='open', traderId=trader_id, negotiator=pawn_id, requireAdjacent=True)
            session = opened['sessionId']
            sale_rows = [row for row in opened['rows'] if row['colonyCount'] > 0 and row['traderWillTrade']
                         and not row['isCurrency'] and not row['isPawn']
                         and sum(r['defName'] == row['defName'] for r in opened['rows']) == 1]
            for row in sale_rows:
                await trade(action='set', sessionId=session, item=row['defName'], count=-row['colonyCount'])
            preview = await trade(action='preview')
            record('trader_affordability_observation', preview=preview)
            assert preview['balance']['traderHasEnoughSilver'] is False, 'Fixture stock cannot exceed this trader budget'
            before = await snapshot()
            record('trader_affordability_refused', refusal=await refused(expected=['trader cannot afford'], action='accept', sessionId=session,
                                                                        dealSignature=preview['dealSignature']))
            assert await snapshot() == before
            for row in sale_rows:
                await trade(action='set', sessionId=session, item=row['defName'], count=0)
            await trade(action='set', sessionId=session, item=sale_rows[0]['defName'], count=-1)
            preview = await trade(action='preview')
            assert preview['wouldSucceed']
            await call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual',
                       ignoreModCompatibility=True, timeoutMs=90000)
            await call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            record('stale_load_refused', refusal=await refused(expected=['No TradeSession', 'load changed'], action='accept', sessionId=session, dealSignature=preview['dealSignature']))
            report['passed'] = True
        except Exception as error:
            report['error'] = repr(error)
            raise
        finally:
            try:
                report['shutdown'] = (await bridge.core('games_stop', gameId=bridge.game_id)).structuredContent
            except Exception as error:
                report['passed'] = False
                report['shutdown_error'] = repr(error)
                raise
            finally:
                (root/'trade-result.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    asyncio.run(run())

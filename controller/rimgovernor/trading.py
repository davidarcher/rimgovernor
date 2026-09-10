"""One deterministic native trade sequence; never infer success from a request."""
import math


async def execute_trade(action, read, write, *, floors=None, stopped=()):
    status=await read({'action':'status'})
    if status.get('sessionActive') is not False:
        raise ValueError('A trade session is active or unknown; do not replace it')
    opened=await write({'action':'open','traderId':action.trader_id,'negotiator':action.negotiator,
        'requireAdjacent':True,'giftMode':False})
    if opened.get('sessionActive') is not True or opened.get('giftMode') is not False:
        raise ValueError('Native trade session was not established')
    session = opened.get('sessionId')
    if not isinstance(session, str) or not session:
        raise ValueError('Native trade session identity is unavailable')
    lines = action.lines
    policy_evidence = []
    if action.policy is not None:
        from .trade_policy import select_trade
        lines, policy_evidence = select_trade(action, opened, floors, stopped)
        if not lines:
            cancelled = await write({'action': 'cancel', 'sessionId': session, 'receiveQuest': False})
            if cancelled.get('sessionActive') is not False:
                raise ValueError('Empty economic trade cancellation is unconfirmed; inspect the session')
            return {'moved': [], 'meaning': 'No affordable eligible trade meets current policy.',
                    'policy': policy_evidence}
    expected=None
    for line in lines:
        staged=await write({'action':'set','sessionId':session,'item':line.item,'count':line.count,'relative':False,'allowPawns':False})
        if staged.get('linesApplied')!=1 or staged.get('linesRejected')!=0:
            raise ValueError('Native trade rejected a line; deal was not accepted')
        expected=staged.get('staged')
    if action.policy is not None:
        sheet = await read({'action': 'sheet'})
        current, _ = select_trade(action, sheet, floors, stopped)
        if sheet.get('sessionId') != session or current != lines:
            raise ValueError('Economic inventory or prices changed; inspect before a new trade')
    preview=await read({'action':'preview'})
    if (preview.get('sessionId') != session or preview.get('traderId')!=opened.get('traderId')
            or preview.get('negotiator')!=opened.get('negotiator')):
        raise ValueError('Trade participants changed; deal was not accepted')
    if (not expected or len([r for r in expected if r.get('isCurrency') is False]) != len(lines)
            or preview.get('staged')!=expected or preview.get('giftMode') is not False):
        raise ValueError('Trade contents changed or are unreadable; deal was not accepted')
    if action.policy is not None and {r.get('defName'): r.get('count') for r in expected
            if r.get('isCurrency') is False} != {line.item: line.count for line in lines}:
        raise ValueError('Native staging differs from selected economic quantities')
    balance=preview.get('balance') or {}
    net=balance.get('netSilverToColony')
    if (preview.get('wouldSucceed') is not True or balance.get('colonyCanAfford') is not True
            or balance.get('traderHasEnoughSilver') is not True or not isinstance(net,(int,float))
            or isinstance(net,bool) or not math.isfinite(net) or net < -action.max_silver_spend):
        raise ValueError('Trade exceeds its budget or native affordability checks failed')
    if action.policy is not None:
        from .trade_policy import number
        reserve = max(action.policy.silver_reserve, (floors or {}).get('Silver', 0))
        if number(balance.get('colonySilverNow')) + net < reserve or ('Silver' in stopped and net < 0):
            raise ValueError('Trade violates the protected silver reserve')
    signature = preview.get('dealSignature')
    if not isinstance(signature, str) or not signature:
        raise ValueError('Native trade preview identity is unavailable')
    accept_args = {'action':'accept','sessionId':session,'dealSignature':signature,'allowEmpty':False,
                   'receiveQuest': False}
    if action.policy is not None:
        native_floors = {t.item: max(t.stock, (floors or {}).get(t.item, 0)) for t in action.policy.targets}
        native_floors['Silver'] = reserve
        accept_args['economicFloors'] = ';'.join(f'{k}={int(v)}' for k, v in sorted(native_floors.items()))
    accepted=await write(accept_args)
    if accepted.get('executed') is not True or accepted.get('actuallyTraded') is not True:
        raise ValueError('Native trade did not confirm an exchange; inspect before retrying')
    if accepted.get('moved')!=expected:
        raise ValueError('Native exchange differs from preview; inspect the actual receipt before any new trade')
    return {'trader':opened.get('traderName'),'moved':accepted['moved'],
        'net_silver':net,'policy':policy_evidence,
        'meaning':'Native deal executed; delivery and hauling require observation.'}

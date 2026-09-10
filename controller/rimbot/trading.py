"""One deterministic native trade sequence; never infer success from a request."""
import math


async def execute_trade(action, read, write):
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
    expected=None
    for line in action.lines:
        staged=await write({'action':'set','sessionId':session,'item':line.item,'count':line.count,'relative':False,'allowPawns':False})
        if staged.get('linesApplied')!=1 or staged.get('linesRejected')!=0:
            raise ValueError('Native trade rejected a line; deal was not accepted')
        expected=staged.get('staged')
    preview=await read({'action':'preview'})
    if (preview.get('sessionId') != session or preview.get('traderId')!=opened.get('traderId')
            or preview.get('negotiator')!=opened.get('negotiator')):
        raise ValueError('Trade participants changed; deal was not accepted')
    if (not expected or len([r for r in expected if r.get('isCurrency') is False]) != len(action.lines)
            or preview.get('staged')!=expected or preview.get('giftMode') is not False):
        raise ValueError('Trade contents changed or are unreadable; deal was not accepted')
    balance=preview.get('balance') or {}
    net=balance.get('netSilverToColony')
    if (preview.get('wouldSucceed') is not True or balance.get('colonyCanAfford') is not True
            or balance.get('traderHasEnoughSilver') is not True or not isinstance(net,(int,float))
            or isinstance(net,bool) or not math.isfinite(net) or net < -action.max_silver_spend):
        raise ValueError('Trade exceeds its budget or native affordability checks failed')
    signature = preview.get('dealSignature')
    if not isinstance(signature, str) or not signature:
        raise ValueError('Native trade preview identity is unavailable')
    accepted=await write({'action':'accept','sessionId':session,'dealSignature':signature,'allowEmpty':False})
    if accepted.get('executed') is not True or accepted.get('actuallyTraded') is not True:
        raise ValueError('Native trade did not confirm an exchange; inspect before retrying')
    if accepted.get('moved')!=expected:
        raise ValueError('Native exchange differs from preview; inspect the actual receipt before any new trade')
    return {'trader':opened.get('traderName'),'moved':accepted['moved'],
        'net_silver':net,'meaning':'Native deal executed; delivery and hauling require observation.'}

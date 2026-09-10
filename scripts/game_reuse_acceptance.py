"""Verify native baseline resets, draft cleanup and stale-client refusal in one game."""
import argparse
import asyncio
import json
from pathlib import Path

from execution_acceptance_smoke import setup
from rimgovernor.native_trials import ReusableGame


async def main(args):
    async with ReusableGame(args.source_root, args.output) as game:
        identities = []
        old = None
        for index in range(3):
            async with game.trial('trial-'+str(index+1)) as rt:
                assert rt.mode == 'manual' and not rt.chat and not rt.manual_requests
                assert rt.store.get('reuse-marker') is None
                identities.append(rt.context_token)
                if old is not None:
                    try:
                        await old.bridge.call('rimworld/set_time_speed', speed='Normal', ultraSpeedBoost=False)
                    except RuntimeError as error:
                        assert 'revoked' in str(error)
                    else:
                        raise AssertionError('Old trial client retained write access')
                evidence = {}
                tool, arguments, _, verify = await setup(rt.game, rt.batch, 'supplies', evidence)
                await rt.game.invoke(tool, arguments, allow_write=True)
                await verify()
                pawn = rt.batch.summary.pawns[0].thing_id
                rt.mode = 'automate'
                await rt.native('home/order', dict(action='draft', pawn=pawn, watch=False, dryRun=False))
                assert rt.draft_owners
                rt.store.set('reuse-marker', 'must not survive')
                rt.manual_requests.append(('unissued-old-step', rt.context_token, rt.chat_revision))
                rt.chat.append({'kind': 'human', 'text': 'Previous trial only'})
                rt.persist()
                (game.output/('trial-'+str(index+1))/'native.json').write_text(json.dumps(evidence, indent=2), encoding='utf8')
                old = rt
            resolved = (await game.bridge.call('home/order', action='resolve', pawn=pawn, dryRun=True)).structuredContent
            assert not resolved['pawn']['drafted'], resolved
            print(json.dumps(game.report['trials'][-1]), flush=True)
        assert len(set(identities)) == 3
        game.report['native_reset_acceptance'] = True
    return True


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, default=Path('.rimgovernor/bridge'))
    parser.add_argument('--output', type=Path, required=True)
    raise SystemExit(0 if asyncio.run(main(parser.parse_args())) else 1)

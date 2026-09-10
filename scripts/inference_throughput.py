"""Compare serial and parallel local inference using the same scored semantic cases."""
import argparse
import asyncio
import hashlib
import json
import time
from pathlib import Path

from rimbot.config import Settings, ModelRole, load_model_routing
from rimbot.model import LocalModel
from rimbot.model_router import ModelRouter
from rimbot.player_commands import semantic_tools
from rimbot.store import Store
from semantic_command_benchmark import CASES, FACTS, score


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    settings = Settings(model=args.model, model_url=args.model_url)
    selected = [case for case in CASES if case[0] in ('research', 'food', 'policy')]*args.repeats
    tools = semantic_tools(resources=FACTS['policyResources'])
    report = dict(measured=False, settings=settings.model_dump(), trials=[], game_writes=0,
        fixture_sha256=hashlib.sha256(json.dumps([selected, FACTS, tools], sort_keys=True).encode()).hexdigest(),
        scope='Fixed-fact local semantic inference throughput; no game simulation or native outcomes')
    path = args.output/'result.json'

    def save():
        path.write_text(json.dumps(report, indent=2), encoding='utf8')

    async def progress(_):
        pass

    for concurrency in (1, 2):
        rows = []
        batch = dict(concurrency=concurrency, cases=rows)
        report['trials'].append(batch)
        began = time.perf_counter()

        async def worker(index):
            store = Store(args.output/f'c{concurrency}-w{index}.sqlite')
            router = ModelRouter(load_model_routing(settings), store, LocalModel)
            try:
                for sequence in range(index, len(selected), concurrency):
                    identity, prompt, expected = selected[sequence]
                    row = dict(sequence=sequence, id=identity, correct=False, worker=index)
                    start = time.perf_counter()
                    try:
                        answer, usage = await router.complete(ModelRole.STRATEGIST, [
                            {'role': 'system', 'content': 'Interpret explicit RimWorld player orders through semantic tools. '
                             'Never claim an order has completed. Facts: '+json.dumps(FACTS)},
                            {'role': 'user', 'content': prompt}], tools, progress)
                        row.update(answer=answer, usage=usage, **score(answer, expected))
                    except Exception as error:
                        row['error'] = repr(error)
                    row['seconds'] = time.perf_counter()-start
                    rows.append(row)
                    save()
                    print(f"concurrency={concurrency} case={identity} correct={row['correct']} seconds={row['seconds']:.2f}", flush=True)
            finally:
                await router.close()
                store.close()

        await asyncio.gather(*(worker(i) for i in range(concurrency)))
        batch['seconds'] = time.perf_counter()-began
        batch['correct'] = sum(row['correct'] for row in rows)
        batch['errors'] = sum('error' in row for row in rows)
        batch['correct_cases_per_minute'] = 60*batch['correct']/batch['seconds']
        batch['completion_tokens_per_second'] = sum(row.get('usage', {}).get('completion_tokens', 0) for row in rows)/batch['seconds']
        save()
    report['measured'] = True
    save()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--model', default='qwen3.5-4b')
    parser.add_argument('--model-url', default='http://127.0.0.1:1234/v1')
    parser.add_argument('--repeats', type=int, default=2)
    args = parser.parse_args()
    if not 1 <= args.repeats <= 100:
        parser.error('Use 1..100 repeats')
    asyncio.run(run(args))

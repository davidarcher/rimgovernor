"""Read-only end-to-end throughput sampling; never starts, pauses or resumes a game."""
import argparse
from collections import Counter
import json
from pathlib import Path
import time
from urllib.request import urlopen


def summarize(samples):
    seconds = ticks = paused = 0
    excluded = 0
    counters = {key: {'count': 0, 'seconds': 0} for key in ('tools', 'actions', 'model_calls')}
    for before, after in zip(samples, samples[1:]):
        elapsed = after['at'] - before['at']
        if (elapsed <= 0 or before.get('error') or after.get('error')
                or not before.get('connected') or not after.get('connected')
                or before.get('session') != after.get('session')
                or type(before.get('tick')) is not int or type(after.get('tick')) is not int
                or after['tick'] < before['tick']):
            excluded += 1
            continue
        seconds += elapsed
        ticks += after['tick'] - before['tick']
        if before.get('paused') and after.get('paused'):
            paused += elapsed
        for key, counter in counters.items():
            first = (before.get('counters') or {}).get(key)
            last = (after.get('counters') or {}).get(key)
            if type(first) is int and type(last) is int and last >= first:
                counter['count'] += last - first
                counter['seconds'] += elapsed
    return {'valid_seconds': round(seconds, 3), 'advanced_ticks': ticks,
            'wall_tps': round(ticks / seconds, 2) if seconds else None,
            'both_endpoints_paused_seconds': round(paused, 3),
            'excluded_intervals': excluded,
            'counter_rates_per_second': {key: round(value['count'] / value['seconds'], 3) if value['seconds'] else None for key, value in counters.items()},
            'observed_stop_reasons': dict(Counter(s['stop_reason'] for s in samples if s.get('stop_reason'))),
            'scope': 'Sampled wall throughput including pauses. Actions are dispatched orders, not completed pawn jobs. Stop-reason counts are samples, not distinct events. Does not certify reaction latency, safety or native job completion.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8787)
    parser.add_argument('--seconds', type=float, default=60)
    parser.add_argument('--interval', type=float, default=2)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 5 <= args.seconds <= 86400 or not .5 <= args.interval <= 60:
        parser.error('Use port 1..65535, seconds 5..86400 and interval .5..60')
    args.output.mkdir(parents=True, exist_ok=False)
    url = f'http://127.0.0.1:{args.port}'
    with urlopen(url + '/api/health', timeout=5) as response:
        health = json.load(response)
    samples = []
    started = time.monotonic()
    report = {'health': health, 'started_utc': time.time(), 'samples': samples}
    try:
        while True:
            sample = {}
            try:
                with urlopen(url + '/api/state', timeout=5) as response:
                    state = json.load(response)
                sample = {'session': state.get('sessionId'), 'connected': state.get('connected') and not state.get('game', {}).get('stale'),
                          'tick': state.get('game', {}).get('tick'), 'paused': state.get('game', {}).get('paused'),
                          'mode': state.get('mode'), 'stop_reason': state.get('clockSupervisor', {}).get('stopReason'),
                          'requested_speed': state.get('clockSupervisor', {}).get('requestedSpeed'),
                          'counters': state.get('counters'), 'controller_status': state.get('currentPlan', {}).get('controller', {}).get('status')}
            except Exception as error:
                sample['error'] = str(error)
            sample['at'] = time.monotonic() - started
            samples.append(sample)
            report['summary'] = summarize(samples)
            (args.output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
            remaining = args.seconds - (time.monotonic() - started)
            if remaining <= 0:
                break
            time.sleep(min(args.interval, remaining))
    finally:
        report['summary'] = summarize(samples)
        (args.output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report['summary'], indent=2))


if __name__ == '__main__':
    main()

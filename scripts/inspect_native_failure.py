"""Inspect a retained native timeline offline or export complete tool fixtures."""
import argparse
import json
from pathlib import Path
import sys

# Offline diagnosis needs only the standard library, including on an uninstalled host.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'controller'))
from rimgovernor.flight_recorder import read_timeline


def inspect(root, export=None):
    root = Path(root)
    rows = list(read_timeline(root/'timeline.jsonl'))
    requests = {r['sequence']: r for r in rows if r['kind']=='native_request'}
    responses = [r for r in rows if r['kind']=='native_response']
    answered = {r['payload'].get('request') for r in responses}
    fixtures = []
    for response in responses:
        payload = response['payload']
        request = requests.get(payload.get('request'))
        if request and not request['payload'].get('truncated') and not payload.get('truncated'):
            fixtures.append(dict(request=request['payload'], response=payload,
                                 context=response['context'], sequence=response['sequence']))
    gaps = [r for r in rows if r['kind']=='recording_gap' or r.get('payload', {}).get('truncated')]
    if not rows:
        gaps.append(dict(kind='recording_gap', reason='Timeline absent: recording disabled or process failed before initialization'))
    outcomes = [r for r in rows if r['kind']=='scenario_outcome']
    summaries = [r for r in rows if r['kind']=='failure_summary']
    summary = dict(records=len(rows), recording_gaps=gaps, last_outcome=outcomes[-1] if outcomes else None,
                   failure_summary=summaries[-1] if summaries else None,
                   last_context=rows[-1].get('context') if rows else None,
                   errors=[r for r in rows if r['kind']=='native_error'],
                   incomplete_requests=[r for key, r in requests.items() if key not in answered],
                   complete_fixtures=len(fixtures), scope='Recorded controller inputs only; no native simulation replay')
    if export:
        with Path(export).open('x', encoding='utf8') as stream:
            json.dump(dict(version=1, scope=summary['scope'], gaps=gaps, fixtures=fixtures), stream, indent=2)
    return summary


def concise_summary(root):
    root = Path(root)
    timeline = inspect(root)
    evidence = {}
    evidence_path = None
    for name in ('scenario-result.json', 'result.json', 'report.json', 'medical-smoke.json'):
        path = root/'scenario'/name
        if path.exists():
            evidence = json.loads(path.read_text(encoding='utf8'))
            evidence_path = str(path.relative_to(root))
            break
    observed = evidence.get('last_observation', evidence.get('initial', {}))
    acquisition = evidence.get('acquisition_progress', {})
    pawns = observed.get('pawns', acquisition.get('pawns', {})).get('pawns', [])
    frame = evidence.get('frame')
    if isinstance(frame, dict):
        frame = {k:frame[k] for k in ('tick','image_sha256','width','height','tick_note') if k in frame}
        frame['path'] = 'scenario/frame.png'
    error = evidence.get('error')
    truncated = isinstance(error, str) and len(error) > 1200
    return dict(version=1, evidence=evidence_path,
        error=error[:1200] if truncated else error, error_truncated=truncated,
        category=evidence.get('category', evidence.get('outcome',
            ('passed' if evidence['passed'] is True else 'failed') if 'passed' in evidence else None)),
        baseline_tick=evidence.get('baseline_tick'), target=evidence.get('target'), actual=evidence.get('actual'),
        last_time=observed.get('time', {'ticksGame':acquisition['tick']} if 'tick' in acquisition else None),
        resources=observed.get('facts', {}).get('resources', acquisition.get('stock')),
        failed_cases=[{k:case[k] for k in ('name','before','after','expected','actual','tick') if k in case}
                      for case in evidence.get('cases', []) if isinstance(case,dict) and case.get('passed') is False],
        pawn_jobs=[{k: p[k] for k in ('thingId', 'name', 'job', 'currentJob', 'blocker') if k in p} for p in pawns],
        frame=frame or evidence.get('frame_omission', 'No rendered frame recorded'),
        checkpoint=evidence.get('failure_checkpoint', evidence.get('checkpoint_omission', 'No failure checkpoint recorded')),
        timeline=dict(path='timeline.jsonl', records=timeline['records'], complete_fixtures=timeline['complete_fixtures'],
            gaps=[{k: gap[k] for k in ('kind', 'sequence', 'reason', 'file', 'line', 'before', 'after') if k in gap}
                  for gap in timeline['recording_gaps']],
            errors=len(timeline['errors']), incomplete_requests=len(timeline['incomplete_requests'])))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--export', type=Path)
    parser.add_argument('--summary', action='store_true', help='Print compact postconditions and evidence links')
    args = parser.parse_args()
    print(json.dumps(concise_summary(args.root) if args.summary else inspect(args.root, args.export), indent=2))

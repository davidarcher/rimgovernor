import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
from types import SimpleNamespace
import xml.etree.ElementTree as ET

SCRIPTS = Path(__file__).resolve().parents[1]/'scripts'
sys.path.insert(0, str(SCRIPTS))
from native_scenarios import backup_databases, classify, junit, resource_summary
from inspect_native_failure import inspect, concise_summary
from rimbot.flight_recorder import FlightRecorder
from native_scenario_support import baseline_tick
import pytest


def test_saved_tick_does_not_parse_unrelated_native_mod_tags(tmp_path):
    save = tmp_path/'save.rws'
    save.write_bytes(b'<save><tickManager>\n<ticksGame>9</ticksGame></tickManager><0>native data</0></save>')
    assert baseline_tick(save)==9
    save.write_bytes(save.read_bytes()*2)
    with pytest.raises(ValueError, match='one saved'):
        baseline_tick(save)


def test_exit_zero_without_outcome_is_not_acceptance(tmp_path):
    assert classify(0, tmp_path)=='infrastructure_failure'
    (tmp_path/'scenario').mkdir()
    result=tmp_path/'scenario/scenario-result.json'
    result.write_text(json.dumps(dict(category='assertion_failure')))
    assert classify(0, tmp_path)=='assertion_failure'
    result.write_text(json.dumps(dict(category='passed')))
    assert classify(1, tmp_path)=='infrastructure_failure'
    assert classify(0, tmp_path)=='passed'
    assert classify(0, tmp_path, True)=='timeout'


def test_backup_includes_committed_wal_and_preserves_source(tmp_path):
    source=tmp_path/'state.sqlite'
    with sqlite3.connect(source) as connection:
        connection.execute('pragma journal_mode=wal')
        connection.execute('create table events (value text)')
        connection.execute("insert into events values ('pre-write intent')")
        connection.commit()
        result=backup_databases(tmp_path)
        assert len(result)==1 and 'error' not in result[0]
        with sqlite3.connect(tmp_path/result[0]['backup']) as snapshot:
            assert snapshot.execute('select value from events').fetchall()==[('pre-write intent',)]
        assert connection.execute('select count(*) from events').fetchone()==(1,)


def test_offline_export_never_invents_missing_receipts(tmp_path):
    recording=FlightRecorder(tmp_path/'timeline.jsonl')
    sequence=recording.event('native_request', tool='read', arguments={})
    recording.event('native_response', request=sequence, result={'native':123})
    recording.event('native_request', tool='write', arguments={'x':1})
    summary=inspect(tmp_path, tmp_path/'fixtures.json')
    assert summary['complete_fixtures']==1
    assert len(summary['incomplete_requests'])==1
    exported=json.loads((tmp_path/'fixtures.json').read_text())
    assert len(exported['fixtures'])==1 and 'no native simulation replay' in exported['scope']


def test_junit_keeps_failed_attempts(tmp_path):
    rows=[dict(scenario='wall',attempt=1,seconds=1,category='timeout'),
          dict(scenario='wall',attempt=2,seconds=2,category='passed')]
    junit(rows,tmp_path/'junit.xml')
    suite=ET.parse(tmp_path/'junit.xml').getroot()
    assert suite.get('tests')=='2' and suite.get('failures')=='1'


def test_missing_timeline_is_an_explicit_gap(tmp_path):
    result=inspect(tmp_path)
    assert result['recording_gaps'] and result['complete_fixtures']==0


def test_offline_summary_needs_no_installed_controller(tmp_path):
    (tmp_path/'scenario').mkdir()
    (tmp_path/'scenario/scenario-result.json').write_text(json.dumps(dict(
        category='assertion_failure', error='Wall still pending', target={'x':4, 'z':5}, actual=[],
        last_observation=dict(time={'ticksGame':609}, facts={'resources':{'WoodLog':5}},
                              pawns={'pawns':[{'thingId':'Pawn_1', 'job':'Wait', 'work':{'types':['x']*10000}}]}))))
    result = subprocess.run([sys.executable, '-S', str(SCRIPTS/'inspect_native_failure.py'),
                             str(tmp_path), '--summary'], capture_output=True, text=True, check=True)
    summary = json.loads(result.stdout)
    assert summary['error']=='Wall still pending'
    assert summary['last_time']['ticksGame']==609
    assert summary['pawn_jobs']==[{'thingId':'Pawn_1', 'job':'Wait'}]
    assert summary['timeline']['gaps']
    assert len(result.stdout)<3000


def test_resource_summary_ignores_failed_samples_and_preserves_units(tmp_path):
    samples = [dict(exit_code=0, sample=json.dumps({'CPUPerc':'125.5%', 'MemUsage':'1.5GiB / 4GiB'})),
               dict(exit_code=0, sample=json.dumps({'CPUPerc':'50%', 'MemUsage':'900MiB / 4GiB'})),
               dict(exit_code=1, sample=''), dict(error='container exited')]
    (tmp_path/'resources.jsonl').write_text('\n'.join(json.dumps(row) for row in samples))
    result = resource_summary(tmp_path)
    assert result['samples']==2
    assert result['mean_sampled_cpu_percent']==87.75
    assert result['peak_sampled_memory_bytes']==round(1.5*1024**3)


def test_summary_bounds_long_native_interruption_error_and_links_original(tmp_path):
    from inspect_native_failure import concise_summary
    (tmp_path/'scenario').mkdir()
    (tmp_path/'scenario/scenario-result.json').write_text(json.dumps({'error':'native window evidence '*3000}))
    result=concise_summary(tmp_path)
    assert len(result['error'])==1200 and result['error_truncated']
    assert result['evidence'].replace('\\','/')=='scenario/scenario-result.json'


@pytest.mark.parametrize('passed, cases, expected', [
    (True, [{'passed':True}], 'passed'),
    (True, [{'passed':False}], 'infrastructure_failure'),
    (False, [{'passed':True}], 'infrastructure_failure'),
    ('true', [{'passed':True}], 'infrastructure_failure'),
    (True, [], 'infrastructure_failure'),
])
def test_mining_adapter_requires_explicit_native_report_and_cases(tmp_path, passed, cases, expected):
    from native_scenarios import classify
    (tmp_path/'scenario').mkdir()
    (tmp_path/'scenario/result.json').write_text(json.dumps(dict(passed=passed,cases=cases)))
    assert classify(0,tmp_path,scenario='mining')==expected


def test_missing_image_retains_structured_preflight_failure(tmp_path, monkeypatch):
    import native_scenarios
    inputs = {}
    for name, relative in [('game','RimWorldLinux'), ('mods',''),
                           ('profile','Saves/RimBot-tribal8-baseline.rws'), ('gabs','gabs')]:
        root = tmp_path/name; root.mkdir(); inputs[name]=root
        if relative:
            path=root/relative; path.parent.mkdir(parents=True, exist_ok=True); path.touch()
    monkeypatch.setattr(native_scenarios, 'docker_environment', lambda: ('docker', {}))
    def unavailable(*args, **kwargs):
        raise subprocess.CalledProcessError(1, args[0], stderr='Image not found')
    monkeypatch.setattr(native_scenarios.subprocess, 'run', unavailable)
    output=tmp_path/'output'
    args=SimpleNamespace(**inputs, output=output, display='headless', scenario=['startup'],
                         no_build=True, image='missing-image')
    assert native_scenarios.run(args) is False
    assert json.loads((output/'result.json').read_text())['results'][0]['category']=='infrastructure_failure'
    assert ET.parse(output/'junit.xml').getroot().get('failures')=='1'


@pytest.mark.parametrize('copy_fails', [False, True])
def test_volume_export_precedes_acceptance_and_preserves_failed_export(tmp_path, monkeypatch, copy_fails):
    import native_scenarios
    inputs = {}
    for name, relative in [('game','RimWorldLinux'), ('mods',''),
                           ('profile','Saves/RimBot-tribal8-baseline.rws'), ('gabs','gabs')]:
        root=tmp_path/name; root.mkdir(); inputs[name]=root
        if relative:
            path=root/relative; path.parent.mkdir(parents=True, exist_ok=True); path.touch()
    calls=[]
    def command(argv, **kwargs):
        calls.append(argv)
        output=''
        if argv[:3]==['docker','image','inspect']:
            output='1' if 'scenario-dashboard' in argv[4] else 'sha256:test'
        elif argv[:4]==['docker','run','--rm','--entrypoint']: output='{}'
        elif argv[:2]==['git','rev-parse']: output='test-revision'
        elif argv[:2]==['docker','inspect']: output=json.dumps([{'State':{'Running':False,'OOMKilled':False}}])
        elif argv[:2]==['docker','cp']:
            if copy_fails: raise subprocess.CalledProcessError(1, argv, stderr='Injected export failure')
            result=Path(argv[-1])/'scenario/scenario-result.json'; result.parent.mkdir()
            result.write_text(json.dumps({'category':'passed'}))
        return subprocess.CompletedProcess(argv,0,stdout=output,stderr='')
    monkeypatch.setattr(native_scenarios,'docker_environment',lambda:('docker',{}))
    monkeypatch.setattr(native_scenarios.subprocess,'run',command)
    args=SimpleNamespace(**inputs,output=tmp_path/'output',display='headless',scenario=['startup'],
        no_build=True,image='test',storage='volume',repeat=1,workers=1,memory='4g',cpus=2,timeout=60,
        gc='0',recording='on',gabs_log_level='info')
    assert native_scenarios.run(args) is not copy_fails
    result=json.loads((args.output/'startup-1/result.json').read_text())
    removed=[call for call in calls if call[:3]==['docker','volume','rm']]
    assert bool(removed) is not copy_fails
    assert result['volume_retained'] is copy_fails
    if not copy_fails:
        assert result['category']=='passed' and result['cleanup']
        assert next(i for i,c in enumerate(calls) if c[:2]==['docker','cp']) < calls.index(removed[0])
    else:
        assert result['category']=='infrastructure_failure' and not result['cleanup']

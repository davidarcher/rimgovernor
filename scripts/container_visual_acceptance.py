"""Run one isolated rendered B14 layout evaluation in local Docker."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import uuid

from container_checks import docker_environment
from container_scenario import dashboard_options, require_dashboard_image


def run(args):
    output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    source=Path(__file__).resolve().parents[1]
    docker,environment=docker_environment()
    name='rimbot-visual-'+uuid.uuid4().hex[:12]
    def command(*values,**kwargs):
        return subprocess.run([docker,*values],cwd=source,env=environment,**kwargs)
    report={'passed':False,'container':name,'scope':'Rendered native blueprint layout, camera ownership, local visual advice and evidence recall; no completed pawn work.'}
    started=time.monotonic()
    try:
        if not args.no_build:
            with (output/'build.log').open('w',encoding='utf8') as log:
                command('build','-f','containers/Dockerfile','--target','worker','-t',args.image,'.',
                        stdout=log,stderr=subprocess.STDOUT,check=True,timeout=1800)
        image=command('image','inspect','--format','{{.Id}}',args.image,capture_output=True,text=True,check=True).stdout.strip()
        report['image']=image
        require_dashboard_image(command, image)
        mounts=[]
        for path,target,readonly in ((args.game,'/inputs/game',True),(args.mods,'/inputs/mods',True),
                                     (args.profile,'/inputs/profile',True),(args.gabs,'/inputs/gabs',True),
                                     (output,'/worker',False)):
            mounts.extend(['--mount',f'type=bind,source={path.resolve()},target={target}'+(',readonly' if readonly else '')])
        with (output/'container.log').open('w',encoding='utf8') as log:
            result=command('run','--name',name,'--init',*dashboard_options(name, 'xvfb'),'--add-host','host.docker.internal:host-gateway',
                '-e','RIMBOT_ALLOW_DOCKER_HOST_MODEL=1','-e','RIMBOT_MODEL_URL=http://host.docker.internal:1234/v1',
                '-e','RIMBOT_MODEL='+args.model,'-e','RIMBOT_DISPLAY=xvfb','-e','RIMBOT_UNITY_GC_TIME_SLICE=0',
                *mounts,image,'--','python','scripts/native_visual_acceptance.py',
                stdout=log,stderr=subprocess.STDOUT,timeout=args.timeout)
        report['exit_code']=result.returncode
        evaluation=output/'evaluation/result.json'
        if evaluation.is_file():
            report['evaluation']=json.loads(evaluation.read_text(encoding='utf8'))
        native=report.get('evaluation',{})
        report['passed']=(result.returncode==0 and native.get('passed') is True
                          and native.get('game_cleanup',{}).get('isError') is False)
    except Exception as error:
        report['error']=repr(error)
    finally:
        report['cleanup_ok']=False
        try:
            cleanup=command('rm','-f',name,capture_output=True,text=True,timeout=60)
            (output/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr,encoding='utf8')
            report['cleanup_ok']=cleanup.returncode==0 or 'No such container' in cleanup.stderr
        except Exception as error:
            report['cleanup_error']=repr(error)
        report['passed']=report['passed'] and report['cleanup_ok']
        report['elapsed_seconds']=time.monotonic()-started
        (output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps({k:v for k,v in report.items() if k!='evaluation'},indent=2),flush=True)
    return report['passed']


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for option in ('game','mods','profile','gabs','output'):
        parser.add_argument('--'+option,type=Path,required=True)
    parser.add_argument('--image',required=True,help='Task-specific worker image tag')
    parser.add_argument('--no-build',action='store_true')
    parser.add_argument('--model',default='qwen3.5-4b')
    parser.add_argument('--timeout',type=int,default=1800)
    raise SystemExit(0 if run(parser.parse_args()) else 1)

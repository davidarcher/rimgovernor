"""Run the B13 native probe in one private local Docker worker; retain failures."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid
from container_checks import docker_environment
from container_scenario import dashboard_options, require_dashboard_image


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('game','mods','profile','gabs','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--image',default='rimbot-b13:worker')
    parser.add_argument('--no-build',action='store_true')
    args=parser.parse_args()
    output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    source=Path(__file__).resolve().parents[1]
    docker,env=docker_environment()
    def call(*command,**kwargs):
        return subprocess.run([docker,*command],env=env,cwd=source,**kwargs)
    if not args.no_build:
        with (output/'build.log').open('w') as log:
            call('build','-f','containers/Dockerfile','--target','worker','-t',args.image,'.',
                 stdout=log,stderr=subprocess.STDOUT,check=True,timeout=1800)
    image=call('image','inspect','--format','{{.Id}}',args.image,capture_output=True,text=True,check=True).stdout.strip()
    name='rimbot-b13-'+uuid.uuid4().hex[:10]
    require_dashboard_image(call, image)
    mounts=[]
    for key in ('game','mods','profile','gabs'):
        mounts+=['--mount',f'type=bind,source={getattr(args,key).resolve()},target=/inputs/{key},readonly']
    probe=source/'scripts/player_actions_acceptance.py'
    mounts+=['--mount',f'type=bind,source={output},target=/worker',
             '--mount',f'type=bind,source={probe},target=/app/scripts/player_actions_acceptance.py,readonly']
    report=dict(image=image,probe_sha256=hashlib.sha256(probe.read_bytes()).hexdigest(),container=name,passed=False)
    try:
        with (output/'container.log').open('w') as log:
            result=call('run','--rm','--init','--name',name,*dashboard_options(name, 'xvfb'),*mounts,image,
                '--display','xvfb','--unity-gc-time-slice','0','--','python','/app/scripts/player_actions_acceptance.py',
                stdout=log,stderr=subprocess.STDOUT,timeout=600)
        report['exit_code']=result.returncode
        native=json.loads((output/'run/player-actions.json').read_text())
        report['passed']=result.returncode==0 and native.get('passed') is True
    finally:
        cleanup=call('rm','-f',name,capture_output=True,text=True,timeout=30)
        report['cleanup_ok']=cleanup.returncode==0 or 'No such container' in cleanup.stderr
        report['passed']=report['passed'] and report['cleanup_ok']
        (output/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr)
        (output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report,indent=2))
    raise SystemExit(0 if report['passed'] else 1)


if __name__=='__main__': main()

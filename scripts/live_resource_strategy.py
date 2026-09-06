"""Optional real-model, read-only strategy check using the production resource observation."""
import asyncio,json,time
from pathlib import Path
import httpx
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.config import Settings

async def main():
    state=httpx.get('http://127.0.0.1:8787/api/state').json()
    assert state['mode']=='manual'
    folder=Path('.rimbot/resource-strategy')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings.model_validate(state['settings']))
    try:
        await rt.poll()
        rt.memory['colony_focus']=state['memory']['colony_focus']
        rt.memory['direction']=['Plan sustainable food and material production from the observed local resources. Compare available options; do not issue orders in this test.']
        start=time.monotonic()
        await asyncio.wait_for(rt.review(strategy=True),150)
        report={'seconds':round(time.monotonic()-start,2),'plans':rt.memory['plans'],'status':rt.status,'counters':rt.counters}
        (folder/'report.json').write_text(json.dumps(report,indent=2))
        print(json.dumps(report))
        assert rt.memory['plans'],'Model did not submit a strategy; see saved trace.'
        assert rt.counters['actions']==0
    finally:
        await rt.stop();rt.store.close()

if __name__=='__main__':asyncio.run(main())

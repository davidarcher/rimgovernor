"""Read-only discovery benchmark against the installed game's native definitions."""
import asyncio
import json
import time
from pathlib import Path
from rimbot.catalog import Catalog
from rimbot.rimapi import RimAPI


async def main():
    api=RimAPI('http://127.0.0.1:8765',Catalog())
    try:
        await api.discover()
        start=time.perf_counter();await api.warm_discovery()
        report={'indexed':len(api.definition_index.records),'build_seconds':time.perf_counter()-start,'results':{},'latency_ms':[]}
        for query in ('free instant sleeping spot','potato','rifle','kitchen'):
            start=time.perf_counter();result=await api.search(query,['construction_place'])
            report['latency_ms'].append((time.perf_counter()-start)*1000)
            report['results'][query]=[hit.model_dump() for hit in result.definitions[:3]]
        spot=report['results']['free instant sleeping spot'][0]
        assert spot['def_name']=='SleepingSpot' and spot['facts']['work_to_build']==0
        assert spot['read']['endpoint']=='construction_definitions'
        assert any('Potato' in hit['def_name'] for hit in report['results']['potato'])
        assert report['results']['rifle'][0]['def_name'].startswith('Gun_')
        report['passed']=True
        path=Path('.rimbot/discovery-live.json');path.parent.mkdir(exist_ok=True)
        path.write_text(json.dumps(report,indent=2))
        print({k:v for k,v in report.items() if k!='results'})
    finally:
        await api.close()


if __name__=='__main__':asyncio.run(main())

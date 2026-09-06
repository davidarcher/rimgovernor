"""Read-only native definition checks for semantic construction selection."""
import asyncio
from rimbot.catalog import Catalog
from rimbot.rimapi import RimAPI

async def main():
    api=RimAPI('http://127.0.0.1:8765',Catalog())
    try:
        await api.discover()
        for name,humanlike in [('Bed',True),('SleepingSpot',True),('AnimalBed',False)]:
            page=await api.call('construction_definitions',{'search':name,'offset':0,'limit':32})
            definition=next(d for d in page.items if d.def_name==name)
            assert definition.is_bed and definition.bed_humanlike is humanlike
            assert definition.description
        print('Native colonist/animal sleeping definitions verified.')
    finally:await api.close()

if __name__=='__main__':asyncio.run(main())

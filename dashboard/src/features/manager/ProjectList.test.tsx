// @vitest-environment jsdom
import {render,screen,fireEvent,waitFor,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import ProjectList from './ProjectList';
afterEach(()=>{cleanup();vi.unstubAllGlobals();});
it('cancels a tracked project without sending game orders',async()=>{
 const fetcher=vi.fn(async()=>({ok:true,json:async()=>({})}));vi.stubGlobal('fetch',fetcher);
 render(<ProjectList projects={[{id:'p1',title:'Shelter',detail:'Beds',state:'pending',evidence:'Awaiting pawn work',matched_ids:[]}]} onChange={()=>{}}/>);
 fireEvent.click(screen.getByLabelText('Cancel Shelter'));
 await waitFor(()=>expect(fetcher).toHaveBeenCalledWith('/api/projects/p1',expect.objectContaining({method:'DELETE'})));
 expect(fetcher).toHaveBeenCalledTimes(1);
});

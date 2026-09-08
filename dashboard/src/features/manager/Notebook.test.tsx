// @vitest-environment jsdom
import {render,screen,fireEvent,waitFor,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import Notebook from './Notebook';
afterEach(()=>{cleanup();vi.unstubAllGlobals();});
const note={id:'poor-soil',text:'Use nearby gravel',evidence:'Terrain query',tick:100,previous_load:true,version:'abc'};
it('shows evidence and sends the displayed note version when forgetting',async()=>{
 const fetcher=vi.fn(async(_url:string,_options:any)=>({ok:true}));vi.stubGlobal('fetch',fetcher);const changed=vi.fn(async()=>{});
 render(<Notebook notes={[note]} sessionId="colony-load" onChange={changed}/>);
 expect(screen.getByText('Terrain query')).toBeTruthy();
 expect(screen.getByText(/previous save load/)).toBeTruthy();
 fireEvent.click(screen.getByRole('button',{name:'Forget poor soil'}));
 await waitFor(()=>expect(changed).toHaveBeenCalledOnce());
 expect(JSON.parse(fetcher.mock.calls[0][1].body)).toEqual({session_id:'colony-load',version:'abc'});
});
it('keeps a rejected note visible and reports the conflict',async()=>{
 vi.stubGlobal('fetch',vi.fn(async()=>({ok:false,json:async()=>({detail:'Note changed; refresh'})})));const changed=vi.fn(async()=>{});
 render(<Notebook notes={[note]} sessionId="colony-load" onChange={changed}/>);
 fireEvent.click(screen.getByRole('button',{name:'Forget poor soil'}));
 expect(await screen.findByRole('alert')).toBeTruthy();
 expect(screen.getByText('Use nearby gravel')).toBeTruthy();expect(changed).not.toHaveBeenCalled();
});


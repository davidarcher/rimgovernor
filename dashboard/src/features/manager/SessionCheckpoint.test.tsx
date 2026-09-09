// @vitest-environment jsdom
import {render,screen,fireEvent,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import SessionCheckpoint from './SessionCheckpoint';
afterEach(()=>{cleanup();vi.unstubAllGlobals();});
it('saves with colony identity and reports verified checkpoint completion',async()=>{
 const fetch=vi.fn().mockResolvedValue({ok:true,json:async()=>({tick:1200})});vi.stubGlobal('fetch',fetch);
 render(<SessionCheckpoint sessionId="colony-load" connected/>);
 fireEvent.click(screen.getByRole('button'));
 expect((await screen.findByRole('status')).textContent).toContain('1200');
 expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({session_id:'colony-load'});
});
it('reports rejected checkpoints without claiming a save',async()=>{
 vi.stubGlobal('fetch',vi.fn().mockResolvedValue({ok:false,json:async()=>({detail:'Colony changed'})}));
 render(<SessionCheckpoint sessionId="old" connected/>);fireEvent.click(screen.getByRole('button'));
 expect((await screen.findByRole('alert')).textContent).toBe('Colony changed');
 expect(screen.queryByRole('status')).toBeNull();
});

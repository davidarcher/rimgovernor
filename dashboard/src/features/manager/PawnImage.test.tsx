// @vitest-environment jsdom
import {act,cleanup,render,screen} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
import PawnImage from './PawnImage';

afterEach(()=>{cleanup();vi.useRealTimers();vi.restoreAllMocks();vi.unstubAllGlobals();});
it('retains the last frame on failures and pause, cancels polling and clears a changed pawn',async()=>{
 vi.useFakeTimers();
 URL.createObjectURL=vi.fn().mockReturnValue('blob:portrait');
 URL.revokeObjectURL=vi.fn();
 const fetch=vi.fn().mockResolvedValueOnce({ok:true,blob:async()=>new Blob(['png']),headers:new Headers({'X-Observed-Tick':'10'})}).mockResolvedValue({ok:false});
 vi.stubGlobal('fetch',fetch);
 const {rerender,unmount}=render(<PawnImage id="Pawn1" name="Sam" sessionId="load-a" active={true} view="follow"/>);
 await act(async()=>{});
 expect(screen.getByRole('img').getAttribute('src')).toBe('blob:portrait');
 await act(async()=>{await vi.advanceTimersByTimeAsync(1000);});
 expect(screen.getByText(/Last image retained/)).toBeTruthy();
 expect(screen.getByRole('img')).toBeTruthy();
 rerender(<PawnImage id="Pawn1" name="Sam" sessionId="load-a" active={false} view="follow"/>);
 await act(async()=>{await vi.advanceTimersByTimeAsync(10000);});
 expect(fetch).toHaveBeenCalledTimes(2);
 expect(screen.getByRole('img')).toBeTruthy();
 rerender(<PawnImage id="Pawn2" name="Owl" sessionId="load-a" active={false} view="follow"/>);
 expect(screen.queryByRole('img')).toBeNull();
 expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:portrait');
 unmount();
});

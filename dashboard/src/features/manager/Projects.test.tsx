import {expect,it,vi,afterEach} from 'vitest';
import {cleanup,fireEvent,render,screen} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import Projects from './Projects';
afterEach(cleanup);
it('cancels the selected project and hides retired entries',()=>{
 const cancel=vi.fn();
 const p={project_id:'a',outcome:'Unwanted room',status:'approved',owner:'Infrastructure',kind:'construction',work_ids:[],constraints:[],success_signals:[]};
 const view=render(<Projects projects={[p]} onCancel={cancel}/>);
 fireEvent.click(screen.getByRole('button',{name:'Cancel Unwanted room'}));
 expect(cancel).toHaveBeenCalledWith('a');
 view.rerender(<Projects projects={[{...p,status:'retired'}]} onCancel={cancel}/>);
 expect(screen.queryByText('Unwanted room')).not.toBeInTheDocument();
 expect(screen.getByText(/Approved objectives appear here/)).toBeVisible();
});

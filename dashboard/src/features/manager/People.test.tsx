// @vitest-environment jsdom
import {render,screen,fireEvent,cleanup} from '@testing-library/react';
import {afterEach,it,expect} from 'vitest';
import People,{type Pawn} from './People';
afterEach(cleanup);
const pawn:Pawn={thing_id:'Pawn1',name:'Sam',position:{x:2,z:3},job:null,drafted:false,downed:false,dead:false,mood:null,food:0,rest:0.5,armed:false,primary_weapon:null,needs_tend:null,bleeding:null};
const observation={pawns:[pawn],start_tick:10,end_tick:12,same_tick:false};
it('distinguishes missing needs from zero and never labels a jobless pawn idle',()=>{
 render(<People observation={observation} stale={true}/>);
 fireEvent.click(screen.getByRole('button',{name:'Sam'}));
 expect(screen.getAllByText('Unknown').length).toBe(3);
 expect(screen.getByText('0%')).toBeTruthy();expect(screen.getByText('50%')).toBeTruthy();
 expect(screen.getByText('No current job')).toBeTruthy();
 expect(screen.getByRole('status').textContent).toContain('last observations');
 expect(screen.getByText(/advanced during collection/)).toBeTruthy();
});
it('does not retain stale pawn details after removal',()=>{
 const {rerender}=render(<People observation={observation} stale={false}/>);
 fireEvent.click(screen.getByRole('button',{name:'Sam'}));
 rerender(<People observation={{...observation,pawns:[]}} stale={false}/>);
 expect(screen.queryByText('50%')).toBeNull();
 expect(screen.getByText(/selected colonist is no longer/)).toBeTruthy();
});

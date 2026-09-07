import {beforeEach,describe,expect,it,vi} from 'vitest';
import {act,cleanup,fireEvent,render,screen} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import Manager from './Manager';

const mocks=vi.hoisted(()=>({act:vi.fn(),state:{connected:true,mode:'manual',busy:false,started_at:null,status:{phase:'Manual'},counters:{tools:0},settings:{},capabilities:100,observation:{game:{game_tick:1000,colonist_count:3},pawns:[]},memory:{plans:{today:['Make the available timber usable'],week:[],season:[],year:[]},goals:[],work:[],chat:[]},activity:[]}}));
vi.mock('./useController',()=>({useController:()=>({state:mocks.state,error:'',setError:vi.fn(),act:mocks.act}),command:vi.fn()}));
vi.mock('./Camera',()=>({default:()=> <div>Camera feed</div>}));
beforeEach(()=>{cleanup();mocks.act.mockReset().mockResolvedValue(true);});

describe('player direction',()=>{
  it('Enter sends, keeps focus and keeps the plan visible',async()=>{
    render(<Manager onInspect={()=>{}}/>);
    const input=screen.getByRole('textbox',{name:'Direction for the colony manager'});
    input.focus();fireEvent.change(input,{target:{value:'Use wood for these walls'}});
    await act(async()=>fireEvent.keyDown(input,{key:'Enter'}));
    expect(mocks.act).toHaveBeenCalledWith('steer',{text:'Use wood for these walls'});
    expect(input).toHaveFocus();expect(input).toHaveValue('');
    expect(screen.getByText('Make the available timber usable')).toBeVisible();
  });
  it('Shift Enter and IME composition do not submit',()=>{
    render(<Manager onInspect={()=>{}}/>);
    const input=screen.getByRole('textbox',{name:'Direction for the colony manager'});
    fireEvent.change(input,{target:{value:'A multiline direction'}});
    fireEvent.keyDown(input,{key:'Enter',shiftKey:true});
    fireEvent.keyDown(input,{key:'Enter',isComposing:true});
    expect(mocks.act).not.toHaveBeenCalled();
  });
  it('a state refresh preserves the unsent direction and editor node',()=>{
    const view=render(<Manager onInspect={()=>{}}/>);
    const input=screen.getByRole('textbox',{name:'Direction for the colony manager'});
    fireEvent.change(input,{target:{value:'Still writing'}});
    view.rerender(<Manager onInspect={()=>{}}/>);
    expect(screen.getByRole('textbox',{name:'Direction for the colony manager'})).toBe(input);
    expect(input).toHaveValue('Still writing');
  });
});

it('keeps work tracking on its own page',()=>{
 render(<Manager view="work" onInspect={()=>{}}/>);
 expect(screen.getByRole('heading',{name:'Work in progress'})).toBeVisible();
 expect(screen.queryByText('Camera feed')).not.toBeInTheDocument();
 expect(screen.queryByRole('textbox',{name:'Direction for the colony manager'})).not.toBeInTheDocument();
 expect(screen.getByRole('link',{name:'Work'})).toHaveAttribute('aria-current','page');
});

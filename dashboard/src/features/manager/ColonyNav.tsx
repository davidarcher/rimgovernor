export type ColonyView='colony'|'base-plan'|'work'|'activity';
export default function ColonyNav({active}:{active:ColonyView}){
 return <nav className="colony-nav" aria-label="Colony navigation">{[['colony','Colony'],['base-plan','Base plan'],['work','Work'],['activity','Activity']].map(([id,label])=><a key={id} href={id==='colony'?'#':'#'+id} aria-current={active===id?'page':undefined}>{label}</a>)}</nav>;
}

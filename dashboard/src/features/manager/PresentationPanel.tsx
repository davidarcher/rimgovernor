import {useEffect, useState} from 'react';
import type {ObservationState} from './observationData';
import {fetchPresentation, PresentationHTTPError, readCamera, readRoster, readSelection, samePresentationWorld, type Camera, type Dossier, type Listing, type PresentationContext, type Roster, type Selection} from './presentationData';
import type {PawnProfile, WorkRoster} from './routineData';
import {useRoutineStatus} from './useRoutineStatus';

type Reading<T> = {key: string; value: T | null; fresh: boolean; hidden: boolean; error: string};
function useReading<T extends {context: PresentationContext}>(kind: 'camera' | 'selection' | 'colonists', read: (value: unknown) => T, observation: ObservationState | null, fresh: boolean): Reading<T> {
  const world = observation?.identity, key = world ? JSON.stringify([observation?.sessionId, world.colonyId, world.mapId, world.loadToken]) : '';
  const enabled = Boolean(key && observation?.connected && !observation.game.stale && fresh);
  const [state, setState] = useState<Reading<T>>({key: '', value: null, fresh: false, hidden: false, error: ''});
  useEffect(() => {
    if (!enabled || !world) {setState(previous => ({...previous, fresh: false})); return;}
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    const poll = async () => {
      try {
        const value = await fetchPresentation(kind, read, AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]));
        if (!samePresentationWorld(value.context.identity, world)) throw Error('Observed world changed; waiting for matching data');
        if (!stopped) setState({key, value, fresh: true, hidden: false, error: ''});
      } catch (error) {
        if (!stopped) setState(previous => ({key, value: previous.key === key ? previous.value : null, fresh: false, hidden: error instanceof PresentationHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'Presentation unavailable'}));
      } finally {if (!stopped) timer = setTimeout(() => void poll(), 1500);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [key, enabled, kind, read]); // Key contains every field used to scope this read.
  if (state.key !== key) return {key, value: null, fresh: false, hidden: false, error: 'Waiting for this world'};
  return {...state, fresh: state.fresh && enabled};
}
const show = (value: string | number | null | undefined) => value ?? 'unknown';
function completeness(value: Listing | null): string {
  if (value?.complete === true) return `Complete · ${show(value.returnedCount)} returned`;
  return `${value?.truncated === true ? 'Truncated' : 'Completeness unknown or partial'} · ${show(value?.returnedCount)} returned of ${show(value?.totalCount)}`;
}
const percent = (value: number | null) => value === null ? 'unknown' : `${Math.round(value * 100)}%`;
const label = (value: {defName: string | null; label: string | null}) => value.label ?? value.defName ?? 'unknown';
const signed = (value: number, digits = 2) => `${value > 0 ? '+' : ''}${value.toFixed(digits).replace(/\.?0+$/, '')}`;
const factor = (value: number) => `×${value.toFixed(2).replace(/\.?0+$/, '')}`;
// The planner's view of the same pawn (#448): what its traits do to work,
// learning and roles, how fast each skill learns, and which skills it lets
// decay. Absent while no review has planned work for this pawn.
function PlannerProfile({profile, decaying}: {profile: PawnProfile; decaying: string[]}) {
  const e = profile.effects;
  const effects = [e.workSpeed !== 0 && `work speed ${signed(e.workSpeed)}`, e.learnRate !== 0 && `learning ${signed(e.learnRate)}`, e.moveSpeed !== 0 && `move speed ${signed(e.moveSpeed)}`, e.sociable !== 0 && `sociable ${signed(e.sociable, 0)}`, e.chemicalInterest !== 0 && `chemical interest ${signed(e.chemicalInterest, 0)}`, ...e.flags].filter(Boolean).join(' · ');
  const roles = [profile.child && 'child', profile.ranged ? 'ranged weapon' : 'no ranged weapon', profile.forbidden.length > 0 && `forbidden: ${profile.forbidden.join(', ')}`, profile.incapable.length > 0 && `incapable: ${profile.incapable.join(', ')}`].filter(Boolean).join(' · ');
  const skills = profile.skills.filter(s => !s.disabled).sort((a, b) => b.level - a.level || a.name.localeCompare(b.name));
  return <>
    <dt>Trait effects</dt><dd>{effects || 'none the planner reads'}</dd>
    <dt>Roles</dt><dd>{roles}</dd>
    <dt>Learning</dt><dd>{skills.length === 0 ? 'no usable skill' : skills.map(s => `${s.name} ${s.level}${s.stored !== s.level ? ` (stored ${s.stored})` : ''} ${factor(s.learnFactor)}${decaying.includes(s.name) ? ' decaying' : ''}`).join(' · ')}</dd>
  </>;
}
function WorkCoverage({roster}: {roster: WorkRoster}) {
  return <details className="presentation-roster"><summary>Work roster · reviewed tick {roster.tick.toLocaleString()}</summary>
    {roster.coverage.length === 0 ? <p>No work types were planned.</p> : <table className="development-table"><thead><tr><th scope="col">Work</th><th scope="col">Owners</th><th scope="col">Wanted</th><th scope="col">Capable</th></tr></thead>
      <tbody>{roster.coverage.map(c => <tr key={c.work} className={c.owners < c.demand ? 'presentation-uncovered' : undefined}><th scope="row">{c.work}</th><td>{c.owners}</td><td>{c.demand}</td><td>{c.capable}</td></tr>)}</tbody></table>}
  </details>;
}
function ColonistDossier({dossier, profile, decaying}: {dossier: Dossier; profile: PawnProfile | null; decaying: string[]}) {
  const state = [dossier.drafted && 'drafted', dossier.downed && 'downed', dossier.inBed && 'in bed', dossier.mentalState && `mental state: ${dossier.mentalState}`].filter(Boolean).join(' · ');
  const skills = dossier.biography.skills.filter(s => !s.disabled).sort((a, b) => (b.level ?? 0) - (a.level ?? 0));
  const hediffs = dossier.health.hediffs.filter(h => h.visible);
  return <dl className="presentation-dossier">
    <dt>Status</dt><dd>{dossier.job ? `Job: ${dossier.job}` : 'Idle'}{state && ` · ${state}`}</dd>
    <dt>Needs</dt><dd>Mood {percent(dossier.needs.mood)}{dossier.needs.breakRisk && dossier.needs.breakRisk !== 'none' && ` (${dossier.needs.breakRisk} break risk)`} · Food {percent(dossier.needs.food)}{dossier.needs.hungerCategory && ` (${dossier.needs.hungerCategory})`} · Rest {percent(dossier.needs.rest)} · Joy {percent(dossier.needs.joy)}</dd>
    <dt>Health</dt><dd>{percent(dossier.health.summaryFraction)}{dossier.health.needsTend && ' · needs tending'}{dossier.health.bleeding && ' · bleeding'}{dossier.health.pain !== null && dossier.health.pain > 0 && ` · pain ${percent(dossier.health.pain)}`}{hediffs.length > 0 && <ul>{hediffs.map((h, i) => <li key={i}>{label(h)}{h.partLabel && ` (${h.partLabel})`}{h.severityLabel && `: ${h.severityLabel}`}</li>)}</ul>}</dd>
    <dt>Biography</dt><dd>{dossier.biography.biologicalAgeYears !== null && `Age ${Math.floor(dossier.biography.biologicalAgeYears)} · `}{dossier.biography.childhood && `${label(dossier.biography.childhood)} · `}{dossier.biography.adulthood && label(dossier.biography.adulthood)}{dossier.biography.traits.length > 0 && <><br/>Traits: {dossier.biography.traits.map(t => t.defName ?? 'unknown').join(', ')}</>}</dd>
    <dt>Skills</dt><dd>{skills.length === 0 ? 'unknown' : skills.map(s => `${label(s)} ${s.level ?? '?'}${s.passion === 'Minor' ? '*' : s.passion === 'Major' ? '**' : ''}`).join(' · ')}</dd>
    {profile && <PlannerProfile profile={profile} decaying={decaying}/>}
    <dt>Gear</dt><dd>{[...dossier.gear.weapons, ...dossier.gear.apparel].length === 0 ? 'none' : [...dossier.gear.weapons, ...dossier.gear.apparel].map(label).join(', ')}</dd>
    {dossier.thoughts.length > 0 && <><dt>Thoughts</dt><dd>{dossier.thoughts.map(t => `${t.label ?? 'unknown'}${t.moodOffsetTotal !== null ? ` (${t.moodOffsetTotal > 0 ? '+' : ''}${Math.round(t.moodOffsetTotal)})` : ''}`).join(' · ')}</dd></>}
  </dl>;
}
function Status<T>({reading}: {reading: Reading<T>}) {return reading.fresh ? null : <p className="presentation-stale" role="status">{reading.value ? 'Stale — last good observation. ' : 'Unavailable. '}{reading.error || 'Waiting for a fresh connection.'}</p>;}
export default function PresentationPanel({observation, observationFresh}: {observation: ObservationState | null; observationFresh: boolean}) {
  const camera = useReading<Camera>('camera', readCamera, observation, observationFresh), selection = useReading<Selection>('selection', readSelection, observation, observationFresh), roster = useReading<Roster>('colonists', readRoster, observation, observationFresh);
  const routines = useRoutineStatus(Boolean(observation?.identity && observation.connected));
  if (!observation?.identity || [camera, selection, roster].every(value => value.hidden)) return null;
  const work = routines.value?.roster ?? null;
  const profiles = new Map((work?.pawns ?? []).map(p => [p.pawn, p]));
  const decaying = (pawn: string | null) => (work?.decaying ?? []).filter(d => d.pawn === pawn).map(d => d.skill);
  return <section className="observation-panel presentation-panel" aria-label="Presentation observations"><h2>Game view</h2><div className="presentation-columns">
    {!camera.hidden && <section aria-label="Camera observation"><h3>Camera</h3><Status reading={camera}/>{camera.value && <><p>Position: {show(camera.value.mapPosition?.x)}, {show(camera.value.mapPosition?.z)}</p><p>Zoom: {show(camera.value.zoomRootSize)} · Root size: {show(camera.value.rootSize)}</p><p>View bounds: {show(camera.value.viewRect?.minX)}, {show(camera.value.viewRect?.minZ)} to {show(camera.value.viewRect?.maxX)}, {show(camera.value.viewRect?.maxZ)}</p><p>Observed tick: {camera.value.context.tick}</p></>}</section>}
    {!selection.hidden && <section aria-label="Selection observation"><h3>Selected objects</h3><Status reading={selection}/>{selection.value && <><p>{completeness(selection.value.listing)}</p>{selection.value.selectedObjects.length === 0 && <p>{selection.value.listing?.complete ? 'Nothing selected.' : 'No selected objects reported; selection may be incomplete.'}</p>}<ul>{selection.value.selectedObjects.map((value, i) => <li key={value.id ?? `unknown-${i}`}><strong>{value.label ?? value.defName ?? value.id ?? 'Unknown object'}</strong><p className="presentation-inspect">{value.inspectLabel ?? 'Inspect label unknown'}{value.inspectText !== null && <><br/>{value.inspectText}</>}</p></li>)}</ul></>}</section>}
    {!roster.hidden && <section aria-label="Colonist observation"><h3>Current-map colonists</h3><Status reading={roster}/>{roster.value && <><p>{completeness(roster.value.listing)}</p>{roster.value.colonists.length === 0 && <p>{roster.value.listing?.complete ? 'No colonists on this map.' : 'No colonists reported; roster may be incomplete.'}</p>}{work && <WorkCoverage roster={work}/>}<ul>{roster.value.colonists.map((value, i) => <li key={value.pawnId ?? `unknown-${i}`}><strong>{value.name ?? value.pawnId ?? 'Unknown colonist'}</strong> · Position: {show(value.position?.x)}, {show(value.position?.z)}{value.dossier && <ColonistDossier dossier={value.dossier} profile={value.pawnId === null ? null : profiles.get(value.pawnId) ?? null} decaying={decaying(value.pawnId)}/>}</li>)}</ul></>}</section>}
  </div></section>;
}

import {useEffect, useRef, useState} from 'react';
import ClockReview from './ClockReview';
import type {ObservationState} from './observationData';
import {PlayerHTTPError, definiteRejection, pauseControl, readBuilding, readControlResult, readCurrentControl, readPlayerSession, readSubmissionResult, resumeControl, sameWorld, submitBuilding, type ControlKind, type ControlRecord, type ControlReply, type ControlRequest, type Submission, type SubmissionRequest, type World} from './playerData';

import {ChatDisabledError, submitChat, type ChatGuidance, type ChatRequest, type ChatReply} from './playerData';

const message = (error: unknown) => error instanceof Error ? error.message : 'Building operation unavailable';
function describeGuidance(value: ChatGuidance): string {
  switch (value.kind) {
    case 'activate_goal': return `Activated goal ${value.goal.goalId} (${value.goal.status}, ${value.goal.need})`;
    case 'cancel_goal': return `Cancelled goal ${value.goal.goalId}`;
    case 'set_population_policy': return `Population policy: up to ${value.populationPolicy.maximum} colonists, ${value.populationPolicy.foodDays} food days`;
    case 'set_expedition_policy': return `Expedition limits: ${Object.entries(value.expeditionPolicy).map(([key, item]) => `${key} ${item}`).join(', ')}`;
    case 'set_population_decision': return `Population decision: ${value.populationDecision.decision} ${value.populationDecision.pawn}`;
    case 'set_resource_policy': return `Resource policy: ${value.resourcePolicy.resource} reserve ${value.resourcePolicy.reserve}, spending ${value.resourcePolicy.spending}`;
  }
}
function submissionMatches(value: Submission, request: SubmissionRequest): boolean {return value.requestId === request.requestId && sameWorld(value.expected, request.expected) && Object.entries(request.building).every(([key, item]) => Object.entries(value.building).some(([other, actual]) => key === other && item === actual));}
function recordMatches(value: ControlRecord, request: ControlRequest, kind: ControlKind): boolean {
  return value.requestId === request.requestId && value.kind === kind && sameWorld(value.expected, request.expected);
}

export default function PlayerControls({observation, observationFresh}: {observation: ObservationState | null; observationFresh: boolean}) {
  const [available, setAvailable] = useState<boolean | null>(null), [token, setToken] = useState<string | null>(null);
  const [current, setCurrent] = useState<ControlReply | null>(null), [currentFresh, setCurrentFresh] = useState(false);
  const [draft, setDraft] = useState({defName: '', stuff: '', x: '', z: '', rotation: 'north'});
  const [submission, setSubmission] = useState<Submission | null>(null), [submitIntent, setSubmitIntent] = useState<SubmissionRequest | null>(null);
  const [chatMessage, setChatMessage] = useState('');
  const [chatReplies, setChatReplies] = useState<Array<{request: ChatRequest; reply: ChatReply}>>([]), [chatIntent, setChatIntent] = useState<ChatRequest | null>(null);
  const [chatSubmitting, setChatSubmitting] = useState(false), [chatDisabled, setChatDisabled] = useState(false);
  const busyChat = useRef(false);
  const [resumeIntent, setResumeIntent] = useState<ControlRequest | null>(null), [pauseIntent, setPauseIntent] = useState<ControlRequest | null>(null);
  const [resumeRecord, setResumeRecord] = useState<ControlRecord | null>(null), [pauseRecord, setPauseRecord] = useState<ControlRecord | null>(null);
  const [history, setHistory] = useState<Array<{kind: ControlKind; request: ControlRequest; record: ControlRecord | null}>>([]);
  const [submitting, setSubmitting] = useState(false), [resuming, setResuming] = useState(false), [pausePending, setPausePending] = useState(false);
  const [rejectedRequests, setRejectedRequests] = useState<string[]>([]);
  const [error, setError] = useState(''), [refreshError, setRefreshError] = useState(''), [bootstrapRetry, setBootstrapRetry] = useState(0);
  const version = useRef(0), mounted = useRef(true), busySubmit = useRef(false), busyResume = useRef(false), busyPause = useRef(false);
  const activeRequests = useRef({resume: '', pause: ''});
  const lastWorld = useRef<World | null>(null), lifetime = useRef(new AbortController());
  if (observation?.identity) lastWorld.current = observation.identity;
  const sessionId = observation?.sessionId ?? '';
  const worldKey = JSON.stringify(observation?.identity ?? null);
  useEffect(() => {version.current++; setCurrentFresh(false);}, [worldKey]);
  useEffect(() => {mounted.current = true; lifetime.current = new AbortController(); return () => {mounted.current = false; lifetime.current.abort();};}, []);
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    version.current++; setToken(null); setCurrentFresh(false);
    const bootstrap = async () => {
      try {
        const next = await readPlayerSession(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]));
        if (stopped) return;
        setToken(next); setAvailable(next !== null); setRefreshError('');
      } catch (reason) {
        if (!stopped) {setRefreshError(message(reason)); timer = setTimeout(() => void bootstrap(), 1500);}
      }
    };
    void bootstrap(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [sessionId, bootstrapRetry]);
  useEffect(() => {
    if (!token) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      const expected = version.current;
      try {
        const next = await readCurrentControl(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]));
        if (!stopped && expected === version.current) {setCurrent(next); setCurrentFresh(next.error === null); setRefreshError(next.error?.detail ?? '');}
      } catch (reason) {if (!stopped && expected === version.current) {setCurrentFresh(false); setRefreshError(message(reason));}}
      finally {if (!stopped) timer = setTimeout(() => void poll(), 1500);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [token, worldKey]);
  const fail = (reason: unknown, expected: number) => {
    if (!mounted.current) return;
    setError(message(reason));
    if (expected === version.current && reason instanceof PlayerHTTPError && reason.status === 403) {setToken(null); setCurrentFresh(false); setBootstrapRetry(value => value + 1);}
  };
  const signal = () => AbortSignal.any([lifetime.current.signal, AbortSignal.timeout(10000)]);
  const showControl = (reply: ControlReply, request: ControlRequest, kind: ControlKind, expected: number) => {
    if (reply.record && !recordMatches(reply.record, request, kind)) throw Error('Control result does not match this request');
    if (!mounted.current) return;
    if (reply.record) {
      if (activeRequests.current[kind] === request.requestId) {if (kind === 'resume') setResumeRecord(reply.record); else setPauseRecord(reply.record);}
      else setHistory(previous => previous.map(item => item.kind === kind && item.request.requestId === request.requestId ? {...item, record: reply.record} : item));
    }
    if (expected === version.current) {
      // Only current reply state describes permission. A historical grant does not.
      setCurrent(previous => ({record: previous?.record ?? null, state: reply.state, error: reply.error}));
      setCurrentFresh(false); // Refresh the current journal record before a new intent.
      setError(reply.error?.detail ?? '');
    }
  };
  const submit = async () => {
    if (!token || busySubmit.current || submitIntent && !submission && !rejectedRequests.includes(submitIntent.requestId) || !observation?.identity || !observationFresh || !observation.connected || observation.game.stale) return;
    const expected = version.current;
    let requestId: string | null = null;
    try {
      if (!draft.x.trim() || !draft.z.trim()) throw Error('Enter both map coordinates');
      const building = readBuilding({...draft, x: Number(draft.x), z: Number(draft.z)});
      const request = {requestId: crypto.randomUUID(), expected: {...observation.identity}, building};
      requestId = request.requestId;
      busySubmit.current = true; setSubmitting(true); setSubmitIntent(request); setSubmission(null); setError('');
      const reply = await submitBuilding(token, request, signal());
      if (!submissionMatches(reply, request)) throw Error('Submission result does not match this request');
      if (mounted.current) setSubmission(reply);
    } catch (reason) {if (mounted.current && requestId && definiteRejection(reason)) {const rejected = requestId; setRejectedRequests(previous => [...previous, rejected]);} fail(reason, expected);} finally {busySubmit.current = false; if (mounted.current) setSubmitting(false);}
  };
  const resume = async () => {
    if (!canResume || !token || !observation?.identity || busyResume.current && resumeIntent && sameWorld(resumeIntent.expected, observation.identity)) return;
    const expected = ++version.current;
    const request: ControlRequest = {requestId: crypto.randomUUID(), expected: {...observation.identity}};
    if (resumeIntent) setHistory(previous => [...previous, {kind: 'resume', request: resumeIntent, record: resumeRecord}]);
    activeRequests.current.resume = request.requestId;
    busyResume.current = true; setResuming(true); setResumeIntent(request); setResumeRecord(null); setCurrentFresh(false); setError('');
    try {showControl(await resumeControl(token, request, signal()), request, 'resume', expected);} catch (reason) {if (mounted.current && definiteRejection(reason)) setRejectedRequests(previous => [...previous, request.requestId]); fail(reason, expected);} finally {if (activeRequests.current.resume === request.requestId) {busyResume.current = false; if (mounted.current) setResuming(false);}}
  };
  const pause = async () => {
    if (!token || !lastWorld.current || busyPause.current) return;
    const expected = ++version.current, request = {requestId: crypto.randomUUID(), expected: {...lastWorld.current}};
    if (pauseIntent) setHistory(previous => [...previous, {kind: 'pause', request: pauseIntent, record: pauseRecord}]);
    activeRequests.current.pause = request.requestId;
    busyPause.current = true; setPausePending(true); setPauseIntent(request); setPauseRecord(null); setCurrentFresh(false); setError('');
    try {showControl(await pauseControl(token, request, signal()), request, 'pause', expected);} catch (reason) {fail(reason, expected);} finally {busyPause.current = false; if (mounted.current) setPausePending(false);}
  };
  const recoverSubmission = async () => {
    if (!submitIntent || busySubmit.current) return;
    busySubmit.current = true; setSubmitting(true); const expected = version.current;
    try {const reply = await readSubmissionResult(submitIntent.requestId, signal()); if (!submissionMatches(reply, submitIntent)) throw Error('Submission result does not match this request'); if (mounted.current) {setSubmission(reply); setError('');}}
    catch (reason) {fail(reason, expected);} finally {busySubmit.current = false; if (mounted.current) setSubmitting(false);}
  };
  const chatMatches = (value: ChatReply, request: ChatRequest) => value.requestId === request.requestId && sameWorld(value.expected, request.expected);
  const sendChat = async () => {
    if (!token || !freshWorld || !observation?.identity || busyChat.current || !chatMessage.trim()) return;
    const expected = version.current; let requestId: string | null = null;
    try {
      const request = {requestId: crypto.randomUUID(), expected: {...observation.identity}, message: chatMessage.trim()};
      requestId = request.requestId; busyChat.current = true; setChatSubmitting(true); setChatIntent(request); setError('');
      const reply = await submitChat(token, request, signal());
      if (!chatMatches(reply, request)) throw Error('Chat reply does not match this request');
      if (mounted.current) {setChatReplies(previous => [...previous.slice(-19), {request, reply}]); setChatMessage('');}
    } catch (reason) {
      if (reason instanceof ChatDisabledError) {if (mounted.current) setChatDisabled(true); return;}
      if (mounted.current && requestId && definiteRejection(reason)) {const rejected = requestId; setRejectedRequests(previous => [...previous, rejected]);}
      fail(reason, expected);
    } finally {busyChat.current = false; if (mounted.current) setChatSubmitting(false);}
  };
  const recoverControl = async (kind: ControlKind, historicalRequest?: ControlRequest) => {
    const request = historicalRequest ?? (kind === 'resume' ? resumeIntent : pauseIntent); if (!request) return;
    const expected = version.current;
    try {showControl(await readControlResult(request.requestId, signal()), request, kind, expected);} catch (reason) {fail(reason, expected);}
  };
  const freshWorld = observationFresh && observation?.connected && !observation.game.stale && observation.identity !== null;
  const sameSubmissionWorld = Boolean(submission && observation?.identity && sameWorld(submission.expected, observation.identity));
  const laterPause = current?.record?.kind === 'pause' && current.record.phase === 'paused' && !current.state.enabled && resumeIntent !== null && sameWorld(current.record.expected, resumeIntent.expected) && current.record.requestId !== resumeIntent.requestId && resumeRecord !== null; // Records are journaled in order: a current pause after a journaled resume supersedes it.
  const resumeInWorld = Boolean(resumeIntent && observation?.identity && sameWorld(resumeIntent.expected, observation.identity));
  const unresolvedResume = resumeInWorld && resumeIntent !== null && !rejectedRequests.includes(resumeIntent.requestId) && (!resumeRecord || ['pending', 'uncertain'].includes(resumeRecord.phase)) && !laterPause;
  const canResume = Boolean(token && freshWorld && currentFresh && observation?.identity && !submitting && !chatSubmitting && !(resuming && resumeInWorld) && !pausePending && !unresolvedResume);
  const generation = current?.state.generation;
  const permissionWorldMatches = Boolean(generation && observation?.identity && sameWorld({colonyId: generation.colony, mapId: generation.map, loadToken: generation.load}, observation.identity));
  const permissionFresh = currentFresh && freshWorld && (!current?.state.enabled || permissionWorldMatches);
  if (available !== true) return refreshError ? <section className="observation-panel building-controls" aria-label="Explicit player controls"><p role="alert">Player controls unavailable: {refreshError}. Retrying connection…</p></section> : null;
  return <section className="observation-panel building-controls" aria-label="Explicit player controls">
	{token && <ClockReview key={`${token}:${worldKey}`} token={token}/>}
    <div className="building-control-heading"><h2>Player controls</h2><button type="button" onClick={() => void resume()} disabled={!canResume}>{resuming && resumeInWorld ? 'Resuming…' : 'Resume'}</button><button type="button" onClick={() => void pause()} disabled={!token || !lastWorld.current || pausePending}>{pausePending ? 'Pausing…' : 'Pause'}</button></div>
    <p>{permissionFresh ? current?.state.enabled ? 'Bot running: orders enabled' : 'Bot paused: orders disabled' : 'Bot state unavailable or refreshing'}</p>
    <p>Submit one building as guidance. While the bot is running it places it alongside its own routine work; native preview and normal game rules determine whether it can be placed.</p>
    <form onSubmit={event => {event.preventDefault(); void submit();}}>
      <div className="building-fields">{(['defName', 'stuff', 'x', 'z'] as const).map(field => <label key={field}>{({defName: 'Definition name', stuff: 'Material (optional)', x: 'Map X', z: 'Map Z'})[field]}<input value={draft[field]} type={field === 'x' || field === 'z' ? 'number' : 'text'} min={field === 'x' || field === 'z' ? 0 : undefined} step={field === 'x' || field === 'z' ? 1 : undefined} onChange={event => setDraft(previous => ({...previous, [field]: event.target.value}))}/></label>)}
        <label>Rotation<select value={draft.rotation} onChange={event => setDraft(previous => ({...previous, rotation: event.target.value}))}>{['north', 'east', 'south', 'west'].map(rotation => <option key={rotation}>{rotation}</option>)}</select></label></div>
      <button type="submit" disabled={!token || !freshWorld || submitting || Boolean(submitIntent && !submission && !rejectedRequests.includes(submitIntent.requestId))}>{submitting ? 'Submitting…' : 'Submit building plan'}</button>
    </form>
    {submitIntent && <p>Submission request: <code>{submitIntent.requestId}</code> {rejectedRequests.includes(submitIntent.requestId) && '· Rejected before admission'} <button type="button" disabled={submitting} onClick={() => void recoverSubmission()}>Check submission result</button></p>}
    {submission && <div><h3>Submitted building</h3><p>{submission.building.defName} · {submission.building.stuff || 'No material specified'} · ({submission.building.x}, {submission.building.z}) · {submission.building.rotation}</p><p>Plan {submission.planId} · Revision {submission.revision}</p>{!sameSubmissionWorld && <p>This submission belongs to a different observed world.</p>}</div>}
    {!chatDisabled && <><h3>Chat</h3>
      <p>Ask the adviser what the autopilot is doing and why, or nudge its policy: activate or cancel a maintained goal, cap the population, set expedition limits, decide for a named pawn, or reserve or restrict a resource. It never places buildings or issues orders.</p>
      <form onSubmit={event => {event.preventDefault(); void sendChat();}}>
        <label>Message<input value={chatMessage} onChange={event => setChatMessage(event.target.value)} placeholder="e.g. why is nobody cooking?"/></label>
        <button type="submit" disabled={!token || !freshWorld || chatSubmitting || !chatMessage.trim()}>{chatSubmitting ? 'Thinking…' : 'Send'}</button>
      </form>
      {chatReplies.length > 0 && <ol className="chat-log" aria-label="Chat replies">{chatReplies.map(({request, reply}) => <li key={request.requestId}><p><strong>You:</strong> {request.message}</p><p><strong>Adviser:</strong> {reply.explanation}</p>{reply.guidance && <p>Applied: {describeGuidance(reply.guidance)}</p>}{observation?.identity && !sameWorld(reply.expected, observation.identity) && <p>This reply belongs to a different observed world.</p>}</li>)}</ol>}
    </>}
    {resumeIntent && <p>Resume request: <code>{resumeIntent.requestId}</code> · Historical result: {resumeRecord?.phase ?? (resumeIntent && rejectedRequests.includes(resumeIntent.requestId) ? 'rejected before admission' : 'not yet known')} <button type="button" onClick={() => void recoverControl('resume')}>Check resume result</button></p>}
    {pauseIntent && <p>Pause request: <code>{pauseIntent.requestId}</code> · Historical result: {pauseRecord?.phase ?? 'not yet known'} <button type="button" onClick={() => void recoverControl('pause')}>Check pause result</button></p>}
    {rejectedRequests.filter(requestId => requestId !== submitIntent?.requestId && requestId !== chatIntent?.requestId && requestId !== resumeIntent?.requestId && !history.some(item => item.request.requestId === requestId)).map(requestId => <p key={requestId}>Rejected before admission: <code>{requestId}</code>. A new explicit request is allowed.</p>)}
    {history.map(item => <p key={`${item.kind}:${item.request.requestId}`}>Previous {item.kind} request: <code>{item.request.requestId}</code> · Historical result: {item.record?.phase ?? (rejectedRequests.includes(item.request.requestId) ? 'rejected before admission' : 'not yet known')} <button type="button" onClick={() => void recoverControl(item.kind, item.request)}>Check previous {item.kind} result</button></p>)}
    {(error || refreshError) && <p role="alert">{error || refreshError}. Draft and request IDs are retained; checking a result only reads it.</p>}
  </section>;
}

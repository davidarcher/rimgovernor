import {useEffect, useRef, useState} from 'react';
import {acknowledgeClock, definiteRejection, readClockReview, type ClockAcknowledgement, type ClockReview as Review} from './playerData';

export default function ClockReview({token}: {token: string}) {
  const [review, setReview] = useState<Review | null>(null);
  const [error, setError] = useState('');
  const [fresh, setFresh] = useState(false);
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<ClockAcknowledgement | null>(null);
  const lifetime = useRef(new AbortController());
  const version = useRef(0);
  const writing = useRef(false);
  useEffect(() => {
    const controller = new AbortController(); lifetime.current = controller;
    let timer: ReturnType<typeof setTimeout>;
    const refresh = async () => {
      if (!writing.current) {
        const serial = ++version.current;
        try {const value = await readClockReview(controller.signal); if (!controller.signal.aborted && serial === version.current) {setReview(value); setFresh(true); setError('');}}
        catch (e) {if (!controller.signal.aborted && serial === version.current) {setFresh(false); setError(e instanceof Error ? e.message : 'Clock review unavailable');}}
      }
      if (!controller.signal.aborted) timer = setTimeout(() => void refresh(), 3000);
    };
    void refresh();
    return () => {controller.abort(); clearTimeout(timer);};
  }, []);
  const acknowledge = async () => {
    if (!review || writing.current) return;
    const request = pending ?? {requestId: crypto.randomUUID(), expectedRevision: review.revision, throughCursor: review.reviewedCursor};
    setPending(request); setBusy(true); writing.current = true; ++version.current;
    try {
      const value = await acknowledgeClock(token, request, lifetime.current.signal);
      if (!lifetime.current.signal.aborted) {setReview(value); setPending(null); setFresh(true); setError('');}
    } catch (e) {
      if (!lifetime.current.signal.aborted) {setError(e instanceof Error ? e.message : 'Acknowledgement unavailable'); setFresh(false); if (definiteRejection(e)) setPending(null);}
    } finally {writing.current = false; if (!lifetime.current.signal.aborted) setBusy(false);}
  };
  if (!review) return null;
  return <section aria-label="Clock interruption review">
    <h3>Clock supervision</h3>
    <p>{review.holds.length ? 'Clock stopped for review. Inspect the game and its notifications before acknowledging.' : 'No captured clock interruptions awaiting acknowledgement.'}</p>
    {review.holds.some(h => h.kind === 'gap') && <p>Some event history is missing. Inspect the colony before continuing.</p>}
    <p>Acknowledging does not resume time or enable orders. Enable the plan separately when ready.</p>
    {error && <p role="alert">{error}</p>}
    <button type="button" disabled={busy || (!pending && (!fresh || review.holds.length === 0))} onClick={() => void acknowledge()}>{busy ? 'Acknowledging…' : pending ? 'Retry acknowledgement' : 'Acknowledge inspected interruptions'}</button>
  </section>;
}

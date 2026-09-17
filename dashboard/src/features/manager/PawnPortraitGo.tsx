import {useEffect, useRef, useState} from 'react';
import {fetchPawnImage, pawnImageObjectURL} from './pawnImageData';

export default function PawnPortraitGo({token, pawnId, name, active}: {token: string | null; pawnId: string; name: string; active: boolean}) {
  const [frame, setFrame] = useState(''), [error, setError] = useState('');
  const retained = useRef('');
  useEffect(() => {
    setFrame(''); setError('');
    return () => {if (retained.current) URL.revokeObjectURL(retained.current); retained.current = '';};
  }, [pawnId, token]);
  useEffect(() => {
    if (!token || !active) return;
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    const poll = async () => {
      try {
        const image = await fetchPawnImage(token, pawnId, 'portrait', controller.signal);
        if (stopped) return;
        const next = pawnImageObjectURL(image);
        if (retained.current) URL.revokeObjectURL(retained.current);
        retained.current = next; setFrame(next); setError('');
      } catch (reason) {if (!stopped) setError(reason instanceof Error ? reason.message : 'Portrait unavailable');}
      if (!stopped) timer = setTimeout(() => void poll(), 15000);
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [token, pawnId, active]);
  return <figure className="pawn-image pawn-image-portrait">
    {frame ? <img src={frame} alt={`${name} portrait`}/> : <div className="pawn-image-empty" aria-label={`${name} portrait unavailable`}>{name.slice(0, 1)}</div>}
    {error && <figcaption>{error}</figcaption>}
  </figure>;
}

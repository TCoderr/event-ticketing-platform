import { useEffect, useRef, useState } from 'react';
import { formatCountdown } from '../lib/format';
import { Status } from './ui';

/** Sunucu saatine göre kalan süre (ms). Tarayıcı saati hesaba girmez. */
export function remainingMs(expiresAt: string, serverTime: string): number {
  return Date.parse(expiresAt) - Date.parse(serverTime);
}

const monotonicNow = () => performance.now();

interface CountdownProps {
  expiresAt: string;
  /** Yanıttaki sunucu saati. */
  serverTime: string;
  /** Yanıtın alındığı an (`now()` biriminde). Sayaç bu andan itibaren monoton saatle ilerler. */
  receivedAt: number;
  /** Süre bittiğinde bir kez çağrılır: sayaç yalnız gösterimdir, gerçek durum sunucudan yeniden okunur. */
  onExpire: () => void;
  now?: () => number;
}

export const ANNOUNCE_AT_SECONDS = [120, 30] as const;

/**
 * Rezervasyon geri sayımı. Kalan süre `expiresAt − serverTime` farkından kurulur ve monoton saatle azaltılır;
 * tarayıcının duvar saati yanlış olsa bile doğru kalır. Sayaç `aria-live="off"`tur; yalnız 2 dakika ve
 * 30 saniye kala (ve süre dolduğunda) ayrı bir `polite` bölge duyuru yapar.
 */
export function Countdown({ expiresAt, serverTime, receivedAt, onExpire, now = monotonicNow }: CountdownProps) {
  const deadline = receivedAt + remainingMs(expiresAt, serverTime);
  const secondsLeft = (at: number) => Math.max(0, Math.ceil((deadline - at) / 1000));
  const [seconds, setSeconds] = useState(() => secondsLeft(now()));
  const [announcement, setAnnouncement] = useState('');
  const previous = useRef(seconds);
  const expired = useRef(false);
  const onExpireRef = useRef(onExpire);

  useEffect(() => {
    onExpireRef.current = onExpire;
  }, [onExpire]);

  useEffect(() => {
    expired.current = false;
    const tick = () => {
      const left = Math.max(0, Math.ceil((deadline - now()) / 1000));
      setSeconds(left);
      for (const mark of ANNOUNCE_AT_SECONDS) {
        if (previous.current > mark && left <= mark && left > 0) {
          setAnnouncement(mark === 120 ? 'Rezervasyonun bitmesine 2 dakika kaldı.' : 'Rezervasyonun bitmesine 30 saniye kaldı.');
        }
      }
      previous.current = left;
      if (left === 0 && !expired.current) {
        expired.current = true;
        setAnnouncement('Rezervasyon süresi doldu.');
        onExpireRef.current();
      }
    };
    tick();
    const timer = setInterval(tick, 250);
    return () => clearInterval(timer);
  }, [deadline, now]);

  const low = seconds > 0 && seconds <= 120;
  const out = seconds === 0;
  return (
    <>
      <div className={`timer${low ? ' is-low' : ''}${out ? ' is-out' : ''}`}>
        <span className="timer-val" role="timer" aria-live="off" aria-label="Kalan süre" data-testid="countdown">{formatCountdown(seconds)}</span>
        <span className="timer-k">
          {out ? 'Rezervasyon sona erdi' : 'Rezervasyon için kalan süre'}{' '}
          {low && <Status tone="warn" icon="clock">Süre azalıyor</Status>}
          {out && <Status tone="warn" icon="clock">Süre doldu</Status>}
        </span>
        <span className="timer-sub">
          {out
            ? 'Sunucudan güncel durum okunuyor…'
            : 'Seçimin süre bitene kadar başkasına verilmez. Bu bir satın alma değildir; ödeme başarısı garanti edilmez.'}
        </span>
      </div>
      <p className="sr-only" aria-live="polite" data-testid="countdown-live">{announcement}</p>
    </>
  );
}

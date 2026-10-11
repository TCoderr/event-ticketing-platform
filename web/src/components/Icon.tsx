import type { ReactNode } from 'react';

export type IconName =
  | 'check' | 'cross' | 'warn' | 'repeat' | 'clock' | 'dots' | 'info' | 'planned' | 'half'
  | 'minus' | 'plus' | 'arrow' | 'scan' | 'offline';

const PATHS: Record<IconName, ReactNode> = {
  check: <path d="M5 12.5l4.5 4.5L19 7.5" />,
  cross: <path d="M6 6l12 12M18 6L6 18" />,
  warn: <><path d="M12 3.5L22 20.5H2z" /><path d="M12 10v5M12 17.7v.1" /></>,
  repeat: <><path d="M4.5 12a7.5 7.5 0 1 0 2.4-5.5" /><path d="M4.5 4v4.5H9" /></>,
  clock: <><circle cx="12" cy="12" r="8.5" /><path d="M12 7.5V12l3 2" /></>,
  dots: <path d="M6 12h.1M12 12h.1M18 12h.1" strokeWidth="3.2" />,
  info: <><circle cx="12" cy="12" r="8.5" /><path d="M12 11v5.5M12 7.6v.1" /></>,
  planned: <circle cx="12" cy="12" r="7.5" />,
  half: <><circle cx="12" cy="12" r="7.5" /><path d="M12 4.5a7.5 7.5 0 0 1 0 15z" fill="currentColor" /></>,
  minus: <path d="M6 12h12" />,
  plus: <path d="M6 12h12M12 6v12" />,
  arrow: <path d="M5 12h14M13 6l6 6-6 6" />,
  scan: <path d="M4 8V5h3M17 5h3v3M20 16v3h-3M7 19H4v-3M4 12h16" />,
  offline: <><path d="M3 9.5a13 13 0 0 1 18 0M6.5 13a8 8 0 0 1 11 0M10 16.5a3 3 0 0 1 4 0" /><path d="M4 4l16 16" /></>,
};

/** Süsleme ikonu: anlam her zaman yanındaki metindedir, bu yüzden yardımcı teknolojiden gizlidir. */
export function Icon({ name }: { name: IconName }) {
  return (
    <svg className="ico" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      {PATHS[name]}
    </svg>
  );
}

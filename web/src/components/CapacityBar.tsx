import type { CSSProperties } from 'react';
import { formatShare } from '../lib/format';

interface CapacityBarProps {
  capacity: number;
  sold: number;
  held: number;
  available: number;
  /** "koltuk" veya "bilet". */
  unit: string;
}

const SEGMENTS = [
  { key: 'sold', label: 'Satıldı', color: 'var(--cap-sold)' },
  { key: 'held', label: 'Tutuluyor', color: 'var(--cap-held)' },
  { key: 'free', label: 'Boş', color: 'var(--cap-free)' },
] as const;

/**
 * Kapasite çubuğu: satıldı / tutuluyor / boş. Kimlik yalnız renkle verilmez: doğrudan etiketler, lejant işlevi
 * gören tablo ve segmentler arası 2px zemin boşluğu vardır. Metin her zaman mürekkep rengindedir.
 * Palet (#2B5F9E / #B8650A / #0F8A78) #F5F0E6 zemininde doğrulanmıştır (dataviz validate_palette.js).
 */
export function CapacityBar({ capacity, sold, held, available, unit }: CapacityBarProps) {
  const values = { sold, held, free: available };
  const heldCenter = capacity > 0 ? ((sold + held / 2) / capacity) * 100 : 50;
  const summary = `Kapasite ${capacity} ${unit}: ${sold} satıldı, ${held} tutuluyor, ${available} boş`;
  return (
    <figure className="cap">
      <div className="cap-plot">
        <span className="cap-dl sold" aria-hidden="true">Satıldı<b>{sold}</b></span>
        <span className="cap-dl free" aria-hidden="true">Boş<b>{available}</b></span>
        <div className={`cap-bar${capacity === 0 ? ' is-empty' : ''}`} role="img" aria-label={summary}>
          {SEGMENTS.filter((s) => values[s.key] > 0).map((s) => (
            <span
              key={s.key}
              className={`seg ${s.key}`}
              style={{ flex: `${values[s.key]} 1 0` }}
              title={`${s.label}: ${values[s.key]} ${unit} (${formatShare(values[s.key], capacity)})`}
            />
          ))}
        </div>
        <span
          className="cap-dl held"
          aria-hidden="true"
          style={{ left: `${heldCenter}%`, transform: `translateX(-${heldCenter}%)`, '--tick': `${heldCenter}%` } as CSSProperties}
        >
          Tutuluyor<b>{held}</b>
        </span>
      </div>
      <table className="cap-tbl">
        <caption className="sr-only">Kapasite dağılımı</caption>
        <thead>
          <tr>
            <th scope="col">Durum</th>
            <th scope="col" className="num">{unit === 'koltuk' ? 'Koltuk' : 'Bilet'}</th>
            <th scope="col" className="num">Pay</th>
          </tr>
        </thead>
        <tbody>
          {SEGMENTS.map((s) => (
            <tr key={s.key}>
              <th scope="row"><span className="sw" style={{ background: s.color }} aria-hidden="true" />{s.label}</th>
              <td className="num">{values[s.key]}</td>
              <td className="num">{formatShare(values[s.key], capacity)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th scope="row">Kapasite</th>
            <td className="num">{capacity}</td>
            <td className="num">{capacity > 0 ? '%100' : '—'}</td>
          </tr>
        </tfoot>
      </table>
    </figure>
  );
}

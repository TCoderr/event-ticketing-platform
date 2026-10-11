import { useMemo } from 'react';
import { formatMoney } from '../lib/format';
import type { PublicSeat } from '../lib/types';
import { Icon } from './Icon';

export const seatLabel = (seat: PublicSeat) => `${seat.section} / ${seat.row} / ${seat.number}`;

export interface SeatToggleResult {
  selected: string[];
  /** Kullanıcıya gösterilecek uyarı (ör. sınır aşıldı); yoksa `null`. */
  message: string | null;
  /** Ekran okuyucu için kısa duyuru; değişiklik yoksa `null`. */
  announcement: string | null;
}

/**
 * Koltuk seçimini değiştirir. Harita ve liste aynı fonksiyonu kullanır; tek durum kaynağı `selected` dizisidir.
 * `want` verilmezse mevcut durum tersine çevrilir.
 */
export function toggleSeat(selected: readonly string[], seat: PublicSeat, max: number, want?: boolean): SeatToggleResult {
  const index = selected.indexOf(seat.id);
  const target = want ?? index < 0;
  if (!target) {
    if (index < 0) return { selected: [...selected], message: null, announcement: null };
    return { selected: selected.filter((id) => id !== seat.id), message: null, announcement: `${seatLabel(seat)} seçimden çıkarıldı.` };
  }
  if (index >= 0) return { selected: [...selected], message: null, announcement: null };
  if (!seat.available) return { selected: [...selected], message: `${seatLabel(seat)} dolu; seçilemez.`, announcement: null };
  if (selected.length >= max) {
    return { selected: [...selected], message: `En fazla ${max} koltuk seçebilirsin. Yeni koltuk için önce birini bırak.`, announcement: null };
  }
  return { selected: [...selected, seat.id], message: null, announcement: `${seatLabel(seat)} seçildi.` };
}

interface SeatPickerProps {
  seats: readonly PublicSeat[];
  selected: readonly string[];
  /** `want` yalnız listeden (onay kutusu) gelir; haritadaki tıklama mevcut durumu çevirir. */
  onToggle: (seat: PublicSeat, want?: boolean) => void;
  disabled?: boolean;
}

interface RowGroup {
  row: string;
  seats: PublicSeat[];
}

interface SectionGroup {
  name: string;
  minY: number;
  prices: number[];
  rows: RowGroup[];
}

function groupSeats(seats: readonly PublicSeat[]): SectionGroup[] {
  const sections = new Map<string, { minY: number; prices: Set<number>; rows: Map<string, PublicSeat[]> }>();
  for (const seat of seats) {
    let section = sections.get(seat.section);
    if (!section) {
      section = { minY: seat.y, prices: new Set(), rows: new Map() };
      sections.set(seat.section, section);
    }
    section.minY = Math.min(section.minY, seat.y);
    section.prices.add(seat.priceMinor);
    const row = section.rows.get(seat.row);
    if (row) row.push(seat);
    else section.rows.set(seat.row, [seat]);
  }
  return [...sections.entries()]
    .map(([name, s]) => ({
      name,
      minY: s.minY,
      prices: [...s.prices].sort((a, b) => a - b),
      rows: [...s.rows.entries()]
        .map(([row, list]) => ({ row, seats: [...list].sort((a, b) => a.number - b.number) }))
        .sort((a, b) => a.seats[0]!.y - b.seats[0]!.y),
    }))
    .sort((a, b) => a.minY - b.minY);
}

const priceText = (prices: number[]) =>
  prices.length === 1 ? formatMoney(prices[0]!) : `${formatMoney(prices[0]!)} – ${formatMoney(prices[prices.length - 1]!)}`;

// Plan ölçüsü: sunucu koordinatı "onda bir koltuk" birimidir; bir koltuk 24 px'lik hücreye çizilir.
const CELL = 24;
const SCALE = CELL / 10;
const SIDE = 30;
const TOP = 54;
const HATCH_ID = 'loca-seat-hatch';

/**
 * Salon planı (fare/dokunma) + eşdeğer koltuk listesi (klavye/ekran okuyucu için birincil yol).
 * İkisi aynı `selected` durumunu gösterir. Durum şekille ayrılır: boş = çerçeve, seçili = dolu + onay, dolu = çapraz çizgi.
 */
export function SeatPicker({ seats, selected, onToggle, disabled = false }: SeatPickerProps) {
  const sections = useMemo(() => groupSeats(seats), [seats]);
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  const byId = useMemo(() => new Map(seats.map((s) => [s.id, s])), [seats]);

  const maxX = seats.reduce((m, s) => Math.max(m, s.x), 0);
  const maxY = seats.reduce((m, s) => Math.max(m, s.y), 0);
  const width = SIDE * 2 + maxX * SCALE + CELL;
  const height = TOP + maxY * SCALE + CELL + 8;

  return (
    <div className="seat-grid">
      <div>
        <h2 id="seat-plan-title">Salon planı</h2>
        {/* Plan yardımcı teknolojiden gizlidir: aynı işlev aşağıdaki listede klavyeyle kullanılabilir. */}
        <div
          className="plan"
          aria-hidden="true"
          style={{ maxWidth: Math.max(280, Math.min(560, width * 1.5)) }}
          onClick={(event) => {
            if (disabled) return;
            const target = (event.target as Element).closest('[data-seat-id]');
            const seat = target ? byId.get(target.getAttribute('data-seat-id') ?? '') : undefined;
            if (seat && (seat.available || selectedSet.has(seat.id))) onToggle(seat);
          }}
        >
          <svg viewBox={`0 0 ${width} ${height}`} focusable="false" data-testid="seat-map">
            <defs>
              <pattern id={HATCH_ID} width="4" height="4" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
                <rect width="4" height="4" fill="#F5F0E6" />
                <line x1="0" y1="0" x2="0" y2="4" stroke="#5A544A" strokeWidth="1.8" />
              </pattern>
            </defs>
            <path
              d={`M${width * 0.14} 8H${width * 0.86}V24Q${width / 2} 38 ${width * 0.14} 24Z`}
              fill="none" stroke="#1C1A17" strokeWidth="1.4"
            />
            <text x={width / 2} y="22" textAnchor="middle" fontSize="10" letterSpacing="3" fontWeight="600">SAHNE</text>
            {sections.map((section) => (
              <g key={section.name}>
                <text x="6" y={TOP + section.minY * SCALE - 6} fontSize="9.5" fontWeight="600" letterSpacing="0.6">
                  {section.name.toLocaleUpperCase('tr')}
                </text>
                <text x={width - 6} y={TOP + section.minY * SCALE - 6} fontSize="9.5" textAnchor="end">{priceText(section.prices)}</text>
                {section.rows.map(({ row, seats: rowSeats }) => {
                  const first = rowSeats[0]!;
                  const last = rowSeats[rowSeats.length - 1]!;
                  const y = TOP + first.y * SCALE + 16;
                  return (
                    <g key={row}>
                      <text x={SIDE + first.x * SCALE - 12} y={y} fontSize="10" textAnchor="middle">{row}</text>
                      <text x={SIDE + last.x * SCALE + CELL + 12} y={y} fontSize="10" textAnchor="middle">{row}</text>
                    </g>
                  );
                })}
              </g>
            ))}
            {seats.map((seat) => {
              const isSelected = selectedSet.has(seat.id);
              const taken = !seat.available && !isSelected;
              return (
                <g
                  key={seat.id}
                  className={`seat${isSelected ? ' is-sel' : ''}${taken ? ' is-taken' : ''}`}
                  data-seat-id={seat.id}
                  data-seat-label={seatLabel(seat)}
                  transform={`translate(${SIDE + seat.x * SCALE},${TOP + seat.y * SCALE})`}
                >
                  <title>{`${seatLabel(seat)} · ${formatMoney(seat.priceMinor)}${taken ? ' · dolu' : ''}`}</title>
                  <rect className="hit" width={CELL} height={CELL} />
                  <rect className="box" x="3" y="3" width="18" height="18" rx="3" style={taken ? { fill: `url(#${HATCH_ID})` } : undefined} />
                  <path className="tick" d="M7.6 12.4l3 3 5.8-6.6" />
                </g>
              );
            })}
          </svg>
        </div>
        <p className="plan-cap">
          Plan fare ve dokunma içindir. Klavye ve ekran okuyucu için koltuk listesini kullan; ikisi aynı seçimi gösterir.
          Müsaitlik ipucudur, kesin sonuç “Koltukları tut” adımında belli olur.
        </p>
      </div>

      <div className="seatlist">
        <h2 id="seat-list-title">Koltuk listesi</h2>
        <p className="note">Sıra bazında gruplanmıştır. Dolu koltuklar seçilemez.</p>
        <div role="group" aria-labelledby="seat-list-title">
          {sections.map((section) => (
            <div key={section.name}>
              <h3>
                <span>{section.name}</span>
                <span className="mono">{priceText(section.prices)}</span>
              </h3>
              {section.rows.map(({ row, seats: rowSeats }) => (
                <fieldset className="rowset" key={row}>
                  <legend>
                    {section.name} · {row} sırası
                    <span className="sr-only">, koltuk başına {priceText([...new Set(rowSeats.map((s) => s.priceMinor))].sort((a, b) => a - b))}</span>
                  </legend>
                  <div className="tiles">
                    {rowSeats.map((seat, index) => {
                      const isSelected = selectedSet.has(seat.id);
                      const taken = !seat.available && !isSelected;
                      const next = rowSeats[index + 1];
                      const aisle = next !== undefined && next.x - seat.x > 10;
                      return (
                        <label className={`st${aisle ? ' gap-after' : ''}`} key={seat.id}>
                          <input
                            type="checkbox"
                            value={seat.id}
                            checked={isSelected}
                            disabled={taken || disabled}
                            aria-label={`Koltuk ${seat.number}${taken ? ', dolu' : ''}`}
                            onChange={(event) => onToggle(seat, event.target.checked)}
                          />
                          <span className="st-box"><span className="st-no">{seat.number}</span></span>
                        </label>
                      );
                    })}
                  </div>
                </fieldset>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

export function SeatLegend({ max }: { max: number }) {
  return (
    <ul className="legend" aria-label="Koltuk durumları">
      <li><span className="lg lg-free" aria-hidden="true" />Boş</li>
      <li><span className="lg lg-sel" aria-hidden="true"><Icon name="check" /></span>Seçili</li>
      <li><span className="lg lg-taken" aria-hidden="true" />Dolu</li>
      <li className="t-mute">En fazla {max} koltuk</li>
    </ul>
  );
}

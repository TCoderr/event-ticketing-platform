import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import type { PublicSeat } from '../lib/types';
import { SeatPicker, toggleSeat } from './SeatPicker';

/** Tek bölüm, iki sıra (A, B), sıra başına `perRow` koltuk; `taken` etiketleri dolu. */
function makeSeats(perRow: number, taken: string[] = []): PublicSeat[] {
  const seats: PublicSeat[] = [];
  ['A', 'B'].forEach((row, rowIndex) => {
    for (let number = 1; number <= perRow; number++) {
      seats.push({
        id: `${row}${number}`, section: 'Salon', row, number, x: (number - 1) * 10, y: rowIndex * 10,
        tierCode: 'S', priceMinor: 45000, available: !taken.includes(`${row}${number}`),
      });
    }
  });
  return seats;
}

/** Gerçek sayfadaki gibi: tek durum kaynağı (`selected`) hem haritayı hem listeyi besler. */
function Harness({ seats, max = 6 }: { seats: PublicSeat[]; max?: number }) {
  const [selected, setSelected] = useState<string[]>([]);
  const [message, setMessage] = useState<string | null>(null);
  return (
    <>
      <SeatPicker
        seats={seats}
        selected={selected}
        onToggle={(seat, want) => {
          const result = toggleSeat(selected, seat, max, want);
          setSelected(result.selected);
          setMessage(result.message);
        }}
      />
      <p role="alert">{message}</p>
      <output data-testid="selected">{selected.join(',')}</output>
    </>
  );
}

const mapSeat = (id: string) => document.querySelector<SVGGElement>(`[data-seat-id="${id}"]`)!;
const row = (name: string) => screen.getByRole('group', { name: new RegExp(`Salon · ${name} sırası`) });
const box = (rowName: string, number: number) => within(row(rowName)).getByRole('checkbox', { name: new RegExp(`^Koltuk ${number}(,|$)`) });

describe('koltuk seçimi: harita ↔ liste', () => {
  it('haritadan seçilen koltuk listede işaretlenir; listeden kaldırılınca haritadan da kalkar', async () => {
    const user = userEvent.setup();
    render(<Harness seats={makeSeats(5)} />);

    await user.click(mapSeat('A3'));
    expect(box('A', 3)).toBeChecked();
    expect(mapSeat('A3')).toHaveClass('is-sel');
    expect(screen.getByTestId('selected')).toHaveTextContent('A3');

    await user.click(box('A', 3));
    expect(box('A', 3)).not.toBeChecked();
    expect(mapSeat('A3')).not.toHaveClass('is-sel');
    expect(screen.getByTestId('selected')).toBeEmptyDOMElement();
  });

  it('listeden seçilen koltuk haritada seçili (dolu + onay) görünür', async () => {
    const user = userEvent.setup();
    render(<Harness seats={makeSeats(5)} />);
    await user.click(box('B', 2));
    expect(mapSeat('B2')).toHaveClass('is-sel');
    expect(mapSeat('B1')).not.toHaveClass('is-sel');
  });

  it('en fazla 6 koltuk: yedinci seçilmez ve açıklayıcı uyarı gösterilir', async () => {
    const user = userEvent.setup();
    render(<Harness seats={makeSeats(5)} />);
    for (const n of [1, 2, 3, 4, 5]) await user.click(box('A', n));
    await user.click(mapSeat('B1'));
    expect(screen.getByTestId('selected')).toHaveTextContent('A1,A2,A3,A4,A5,B1');

    await user.click(box('B', 2));
    expect(box('B', 2)).not.toBeChecked();
    expect(mapSeat('B2')).not.toHaveClass('is-sel');
    expect(screen.getByRole('alert')).toHaveTextContent('En fazla 6 koltuk seçebilirsin');

    // Haritadan da aşılamaz.
    await user.click(mapSeat('B3'));
    expect(screen.getByTestId('selected')).toHaveTextContent('A1,A2,A3,A4,A5,B1');

    // Biri bırakılınca yenisi seçilebilir.
    await user.click(box('A', 1));
    await user.click(box('B', 2));
    expect(box('B', 2)).toBeChecked();
    expect(screen.getByRole('alert')).toBeEmptyDOMElement();
  });

  it('dolu koltuk seçilemez: listede devre dışı ve adında "dolu", haritada çapraz çizgili', async () => {
    const user = userEvent.setup();
    render(<Harness seats={makeSeats(5, ['A2'])} />);
    const taken = within(row('A')).getByRole('checkbox', { name: 'Koltuk 2, dolu' });
    expect(taken).toBeDisabled();
    expect(mapSeat('A2')).toHaveClass('is-taken');

    await user.click(mapSeat('A2'));
    await user.click(taken);
    expect(taken).not.toBeChecked();
    expect(screen.getByTestId('selected')).toBeEmptyDOMElement();
  });

  it('yalnız klavyeyle seçim: Tab ile koltuğa gel, Boşluk ile seç/bırak; dolu koltuk atlanır', async () => {
    const user = userEvent.setup();
    render(<Harness seats={makeSeats(3, ['A2'])} />);

    await user.tab();
    expect(box('A', 1)).toHaveFocus();
    await user.keyboard(' ');
    expect(box('A', 1)).toBeChecked();
    expect(mapSeat('A1')).toHaveClass('is-sel');

    await user.tab(); // A2 dolu: odak A3'e geçer.
    expect(box('A', 3)).toHaveFocus();
    await user.keyboard(' ');
    expect(screen.getByTestId('selected')).toHaveTextContent('A1,A3');

    await user.keyboard(' ');
    expect(box('A', 3)).not.toBeChecked();
    expect(screen.getByTestId('selected')).toHaveTextContent('A1');
  });

  it('harita yardımcı teknolojiden gizlidir; liste sıra bazında gruplanmış onay kutularıdır', () => {
    render(<Harness seats={makeSeats(4)} />);
    expect(screen.getByTestId('seat-map').closest('[aria-hidden="true"]')).not.toBeNull();
    expect(within(row('A')).getAllByRole('checkbox')).toHaveLength(4);
    expect(within(row('B')).getAllByRole('checkbox')).toHaveLength(4);
  });
});

describe('toggleSeat', () => {
  const seats = makeSeats(4, ['B4']);
  const seat = (id: string) => seats.find((s) => s.id === id)!;

  it('seçim ve bırakma için ekran okuyucu duyurusu üretir', () => {
    const added = toggleSeat([], seat('A1'), 6);
    expect(added).toMatchObject({ selected: ['A1'], message: null, announcement: 'Salon / A / 1 seçildi.' });
    const removed = toggleSeat(added.selected, seat('A1'), 6);
    expect(removed).toMatchObject({ selected: [], announcement: 'Salon / A / 1 seçimden çıkarıldı.' });
  });

  it('dolu koltuğu ve sınır aşımını reddeder; mevcut seçimi değiştirmez', () => {
    expect(toggleSeat(['A1'], seat('B4'), 6)).toMatchObject({ selected: ['A1'], message: 'Salon / B / 4 dolu; seçilemez.' });
    expect(toggleSeat(['A1', 'A2'], seat('A3'), 2).selected).toEqual(['A1', 'A2']);
    expect(toggleSeat(['A1', 'A2'], seat('A3'), 2).message).toMatch(/En fazla 2 koltuk/);
  });

  it('istenen durum zaten geçerliyse hiçbir şey yapmaz', () => {
    expect(toggleSeat(['A1'], seat('A1'), 6, true)).toMatchObject({ selected: ['A1'], message: null, announcement: null });
    expect(toggleSeat([], seat('A1'), 6, false)).toMatchObject({ selected: [], message: null, announcement: null });
  });
});

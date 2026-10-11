import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { lateSuccessNote, orderNeedsPolling, orderPollDelay, orderStatusLabel, orderView } from '../lib/orderStatus';
import type { Order } from '../lib/types';
import { OrderStatusPanel } from './OrderStatusPanel';

function order(overrides: Partial<Order> = {}): Order {
  return {
    id: 'o1', orderNumber: 'LC-7K2M4QXD00', status: 'PendingPayment', totalMinor: 90000, currency: 'TRY',
    createdAt: '2026-11-01T10:00:00Z', paidAt: null, closedAt: null, cancelReason: null, holdId: 'h1', holdExpiresAt: '2026-11-01T10:10:00Z',
    sessionId: 's1', eventTitle: 'Kış Uykusundan Önce', venueName: 'Perde Sahnesi', city: 'İstanbul', startsAt: '2026-11-20T17:30:00Z',
    startsAtLocal: '2026-11-20T20:30', timezone: 'Europe/Istanbul', sessionStatus: 'Published', ticketCount: 2,
    lines: [{ lineNo: 1, kind: 'Seat', label: 'Salon / E / 7', quantity: 1, unitPriceMinor: 45000, seatId: 'x', categoryId: null }],
    payment: { status: 'Initiated', failureKind: null, failureCode: null, lateSuccess: false }, refund: null,
    canCancel: false, cancelDeadline: '2026-11-19T17:30:00Z', serverTime: '2026-11-01T10:00:01Z', note: '', isDemo: false,
    ...overrides,
  };
}

const succeeded = { status: 'Succeeded', failureKind: null, failureCode: null, lateSuccess: false } as const;

const CASES: [string, Order, string, string][] = [
  ['checkout sonrası (202)', order(), 'Ödeme işleniyor', 'processing'],
  ['sağlayıcı asenkron', order({ payment: { status: 'Pending', failureKind: null, failureCode: null, lateSuccess: false } }), 'Ödeme işleniyor', 'processing'],
  ['sağlayıcı yanıtı kayıp', order({ payment: { status: 'Unknown', failureKind: null, failureCode: 'timeout', lateSuccess: false } }), 'Sonuç belirsiz', 'unknown'],
  ['ödeme onaylandı', order({ status: 'Paid', payment: succeeded }), 'Ödendi', 'paid'],
  ['kart reddi', order({ status: 'Failed', payment: { status: 'Declined', failureKind: 'Declined', failureCode: 'card_declined', lateSuccess: false } }), 'Reddedildi', 'declined'],
  ['sağlayıcı hatası', order({ status: 'Failed', payment: { status: 'Failed', failureKind: 'ProviderError', failureCode: 'provider_error', lateSuccess: false } }), 'Ödeme alınamadı', 'payment-failed'],
  ['hold süresi doldu', order({ status: 'Expired' }), 'Süre doldu', 'expired'],
  ['iptal, iade sırada', order({ status: 'Cancelled', cancelReason: 'Customer', payment: succeeded, refund: { status: 'Pending', reason: 'CustomerCancellation', amountMinor: 90000 } }), 'İptal edildi · iade bekliyor', 'cancelled-refund-pending'],
  ['iptal, iade tamam', order({ status: 'Cancelled', cancelReason: 'Customer', payment: succeeded, refund: { status: 'Succeeded', reason: 'CustomerCancellation', amountMinor: 90000 } }), 'İptal edildi · iade tamamlandı', 'cancelled-refunded'],
  ['iptal, iade başarısız', order({ status: 'Cancelled', cancelReason: 'SessionCancelled', payment: succeeded, refund: { status: 'Failed', reason: 'SessionCancellation', amountMinor: 90000 } }), 'İptal edildi · iade başarısız', 'cancelled-refund-failed'],
  ['ödeme sonuçlanmadan seans iptali', order({ status: 'Cancelled', cancelReason: 'SessionCancelled' }), 'İptal edildi', 'cancelled'],
];

describe('sipariş durum etiketleri', () => {
  it.each(CASES)('%s → "%s"', (_name, value, title, kind) => {
    expect(orderStatusLabel(value)).toBe(title);
    expect(orderView(value).kind).toBe(kind);
    render(<OrderStatusPanel order={value} />);
    expect(screen.getByRole('heading', { name: title })).toBeInTheDocument();
    expect(screen.getByTestId('order-status')).toHaveAttribute('data-kind', kind);
  });

  it('202 sonrası hiçbir zaman "Ödendi" gösterilmez; panel meşgul olarak işaretlenir', () => {
    render(<OrderStatusPanel order={order()} />);
    const panel = screen.getByTestId('order-status');
    expect(panel).toHaveAttribute('aria-busy', 'true');
    expect(panel).not.toHaveTextContent('Ödendi');
    expect(panel).toHaveTextContent('Henüz tahsilat onayı yok');
  });

  it('belirsiz sonuç ret olarak sunulmaz', () => {
    render(<OrderStatusPanel order={order({ payment: { status: 'Unknown', failureKind: null, failureCode: null, lateSuccess: false } })} />);
    const panel = screen.getByTestId('order-status');
    expect(panel).toHaveTextContent('Bu bir ret değildir');
    expect(panel).toHaveTextContent('Sonuç bekleniyor');
    expect(panel).not.toHaveTextContent('Reddedildi');
  });

  it('iade durumu tutarla birlikte açıklanır', () => {
    render(<OrderStatusPanel order={CASES[7]![1]} />);
    expect(screen.getByTestId('order-status')).toHaveTextContent('900,00 ₺ tutarındaki iade sırada. Tutar henüz geri ödenmedi.');
  });

  it('seans iptalinde neden belirtilir', () => {
    expect(orderView(CASES[9]![1]).body[0]).toBe('Seans organizatör tarafından iptal edildi; biletler artık geçersiz.');
  });

  it('geç başarı telafisi notu: süre dolduktan sonra gelen tahsilat iade edilir, bilet verilmez', () => {
    const late = order({
      status: 'Expired', payment: { status: 'Succeeded', failureKind: null, failureCode: null, lateSuccess: true },
      refund: { status: 'Pending', reason: 'LatePaymentCompensation', amountMinor: 90000 },
    });
    expect(orderStatusLabel(late)).toBe('Süre doldu');
    expect(lateSuccessNote(late)).toMatch(/geç başarı/);
    render(<OrderStatusPanel order={late} />);
    expect(screen.getByTestId('late-success-note')).toHaveTextContent('Bilet verilmedi; 900,00 ₺ tutarındaki telafi iadesi sırada.');
    expect(lateSuccessNote(order({ status: 'Expired' }))).toBeNull();
  });
});

describe('sipariş yoklaması', () => {
  it('sonuç kesinleşene kadar sürer', () => {
    expect(orderNeedsPolling(order())).toBe(true);
    expect(orderNeedsPolling(CASES[2]![1])).toBe(true);
    expect(orderNeedsPolling(CASES[3]![1])).toBe(false);
    expect(orderNeedsPolling(CASES[4]![1])).toBe(false);
    expect(orderNeedsPolling(CASES[7]![1])).toBe(true); // iade bekliyor
    expect(orderNeedsPolling(CASES[8]![1])).toBe(false);
  });

  it('kapanmış siparişin ödemesi hâlâ açıksa (geç başarı gelebilir) sürer', () => {
    expect(orderNeedsPolling(order({ status: 'Expired', payment: { status: 'Pending', failureKind: null, failureCode: null, lateSuccess: false } }))).toBe(true);
    expect(orderNeedsPolling(order({ status: 'Expired', payment: { status: 'Declined', failureKind: 'Declined', failureCode: null, lateSuccess: false } }))).toBe(false);
  });

  it('aralık 1 saniyeden başlar, geri çekilir ve 5 saniyede sabitlenir', () => {
    const delays = [0, 1, 2, 3, 4, 5, 10].map(orderPollDelay);
    expect(delays[0]).toBe(1000);
    expect(delays).toEqual([...delays].sort((a, b) => a - b));
    expect(delays[1]).toBeGreaterThan(1000);
    expect(delays[delays.length - 1]).toBe(5000);
  });
});

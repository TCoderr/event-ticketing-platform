import type { IconName } from '../components/Icon';
import { formatMoney } from './format';
import type { Order, RefundStatus } from './types';

export type OrderTone = 'ok' | 'warn' | 'err' | 'info' | 'mute';

export type OrderViewKind =
  | 'processing' | 'unknown' | 'paid' | 'declined' | 'payment-failed' | 'expired'
  | 'cancelled-refund-pending' | 'cancelled-refunded' | 'cancelled-refund-failed' | 'cancelled';

export interface OrderView {
  kind: OrderViewKind;
  tone: OrderTone;
  icon: IconName;
  title: string;
  body: string[];
  /** Sonuç henüz kesin değil: ekran yoklamaya devam eder. */
  busy: boolean;
}

const REFUND_LABEL: Record<RefundStatus, string> = { Pending: 'iade bekliyor', Succeeded: 'iade tamamlandı', Failed: 'iade başarısız' };

function refundSentence(status: RefundStatus, amount: string): string {
  if (status === 'Pending') return `${amount} tutarındaki iade sırada. Tutar henüz geri ödenmedi.`;
  if (status === 'Succeeded') return `${amount} ödeme yöntemine iade edildi.`;
  return `İade denemesi başarısız oldu. ${amount} henüz geri ödenmedi; iade yeniden denenecek ve operasyon kuyruğunda izleniyor.`;
}

/** Geç başarı telafisi: sipariş kapandıktan sonra gelen tahsilat bileti geri getirmez, tutar iade edilir. */
export function lateSuccessNote(order: Order): string | null {
  if (!order.payment.lateSuccess) return null;
  const amount = formatMoney(order.refund?.amountMinor ?? order.totalMinor);
  const refund = order.refund
    ? order.refund.status === 'Succeeded' ? `${amount} iade edildi.`
      : order.refund.status === 'Pending' ? `${amount} tutarındaki telafi iadesi sırada.`
        : `${amount} tutarındaki telafi iadesi başarısız oldu; yeniden denenecek.`
    : `${amount} tutarındaki telafi iadesi planlanıyor.`;
  return `Ödeme sağlayıcısı tahsilatı sipariş kapandıktan sonra bildirdi (geç başarı). Bilet verilmedi; ${refund}`;
}

/** Sunucudaki sipariş + ödeme + iade durumunu ekrandaki tek duruma çevirir. Sahte başarı üretmez. */
export function orderView(order: Order): OrderView {
  const amount = formatMoney(order.totalMinor);
  switch (order.status) {
    case 'PendingPayment':
      if (order.payment.status === 'Unknown') {
        return {
          kind: 'unknown', tone: 'warn', icon: 'warn', title: 'Sonuç belirsiz', busy: true,
          body: [
            'Ödeme sağlayıcısından kesin yanıt alınamadı. Bu bir ret değildir: tahsilat yapılmış da olabilir, yapılmamış da. Sonuç bekleniyor.',
            'Yeniden ödeme yapma. Sonuç doğrulandığında bu ekran kendiliğinden güncellenir; tahsilat yapıldığı hâlde bilet verilemezse tutar iade edilir.',
          ],
        };
      }
      return {
        kind: 'processing', tone: 'info', icon: 'dots', title: 'Ödeme işleniyor', busy: true,
        body: [
          'Ödeme sağlayıcısının sonucu bekleniyor. Henüz tahsilat onayı yok; biletler ödeme onaylanınca oluşur.',
          'Bu sayfayı kapatabilirsin: sonuç sipariş kaydına işlenir. Yeniden ödeme başlatma.',
        ],
      };
    case 'Paid':
      return {
        kind: 'paid', tone: 'ok', icon: 'check', title: 'Ödendi', busy: false,
        body: [`${amount} tahsil edildi. ${order.ticketCount} bilet bu siparişe ait.`],
      };
    case 'Failed':
      if (order.payment.failureKind === 'Declined') {
        return {
          kind: 'declined', tone: 'err', icon: 'cross', title: 'Reddedildi', busy: false,
          body: ['Ödeme sağlayıcısı ödemeyi reddetti. Tahsilat yapılmadı ve bilet oluşmadı.', 'Yeniden denemek için seçim adımına dön; müsaitlik değişmiş olabilir.'],
        };
      }
      return {
        kind: 'payment-failed', tone: 'err', icon: 'cross', title: 'Ödeme alınamadı', busy: false,
        body: ['Ödeme sağlayıcısında bir hata oluştu; bu bir kart reddi değildir. Tahsilat yapılmadı ve bilet oluşmadı.', 'Yeniden denemek için seçim adımına dön.'],
      };
    case 'Expired':
      return {
        kind: 'expired', tone: 'mute', icon: 'clock', title: 'Süre doldu', busy: false,
        body: [
          'Rezervasyon süresi ödeme onaylanmadan bitti. Seçimin serbest bırakıldı; bilet oluşmadı.',
          'Sonradan bir tahsilat bildirimi gelirse tutarın tamamı iade edilir.',
        ],
      };
    case 'Cancelled': {
      const reason = order.cancelReason === 'SessionCancelled'
        ? 'Seans organizatör tarafından iptal edildi; biletler artık geçersiz.'
        : 'Sipariş iptal edildi; biletler artık geçersiz.';
      if (!order.refund) {
        return {
          kind: 'cancelled', tone: 'mute', icon: 'cross', title: 'İptal edildi', busy: false,
          body: [reason, 'Bu sipariş için onaylanmış bir tahsilat yok; iade gerekmiyor.'],
        };
      }
      const status = order.refund.status;
      return {
        kind: status === 'Pending' ? 'cancelled-refund-pending' : status === 'Succeeded' ? 'cancelled-refunded' : 'cancelled-refund-failed',
        tone: status === 'Pending' ? 'info' : status === 'Succeeded' ? 'ok' : 'err',
        icon: status === 'Pending' ? 'dots' : status === 'Succeeded' ? 'check' : 'cross',
        title: `İptal edildi · ${REFUND_LABEL[status]}`,
        busy: status === 'Pending',
        body: [reason, refundSentence(status, formatMoney(order.refund.amountMinor))],
      };
    }
  }
}

/** Liste satırları için kısa durum etiketi. */
export function orderStatusLabel(order: Order): string {
  return orderView(order).title;
}

/**
 * Yoklama gerekli mi? Ödeme sonucu kesinleşmediyse, iade bekliyorsa ya da kapanmış siparişin ödemesi hâlâ
 * açıksa (geç başarı gelebilir) evet.
 */
export function orderNeedsPolling(order: Order): boolean {
  if (order.status === 'PendingPayment') return true;
  if (order.refund?.status === 'Pending') return true;
  const paymentOpen = order.payment.status === 'Initiated' || order.payment.status === 'Pending' || order.payment.status === 'Unknown';
  return order.status !== 'Paid' && paymentOpen;
}

/** Sipariş yoklama aralığı: 1 sn'den başlar, her denemede 1,5 katına çıkar, 5 sn'de sabitlenir. */
export function orderPollDelay(attempt: number): number {
  return Math.min(5000, Math.round(1000 * 1.5 ** Math.max(0, attempt)));
}

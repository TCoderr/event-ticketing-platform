import type { ReactNode } from 'react';
import { lateSuccessNote, orderView } from '../lib/orderStatus';
import type { Order } from '../lib/types';
import { Icon } from './Icon';

/** Sipariş durumu paneli. Durum her zaman sunucudaki sipariş + ödeme + iade kaydından türetilir. */
export function OrderStatusPanel({ order, children }: { order: Order; children?: ReactNode }) {
  const view = orderView(order);
  const late = lateSuccessNote(order);
  return (
    <div className="ost" data-tone={view.tone} data-kind={view.kind} aria-live="polite" aria-busy={view.busy || undefined} data-testid="order-status">
      <div className="ost-head">
        <span className="ost-ico"><Icon name={view.icon} /></span>
        <div>
          <p className="ost-code">Sipariş durumu</p>
          <h2 className="ost-title">{view.title}</h2>
        </div>
      </div>
      <div className="ost-body">
        {view.body.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
        {late && <p data-testid="late-success-note"><strong>Geç başarı telafisi:</strong> {late}</p>}
      </div>
      {children}
    </div>
  );
}

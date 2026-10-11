// Compose smoke testinin iş akışı adımları: gerçek HTTPS üzerinden seans oluşturma → hold → replay →
// ödeme → bilet → check-in, belirsiz ödemenin kurtarılması ve yeniden başlatma sonrası kalıcılık.
// scripts/smoke.mjs tarafından yüklenir.

const state = {};

function istanbulLocal(date) {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Europe/Istanbul', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(date).map((p) => [p.type, p.value]));
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

async function pollOrder(client, orderId, predicate, timeoutMs) {
  const started = Date.now();
  let last = null;
  while (Date.now() - started < timeoutMs) {
    last = (await client.get(`/api/orders/${orderId}`)).json;
    if (last && predicate(last)) return last;
    await new Promise((r) => setTimeout(r, 1000));
  }
  return last;
}

export function steps(trackSecret) {
  return [
    async ({ sessions, check, idem }) => {
      const organizer = sessions['organizer-kumru@loca.demo'];
      const organizationId = (await organizer.get('/api/organizer/organizations')).json[0].id;
      const suffix = Date.now().toString(36);

      const venue = await organizer.post(`/api/organizer/organizations/${organizationId}/venues`,
        { name: `Smoke Salonu ${suffix} (Demo)`, city: 'İstanbul', address: 'Demo Caddesi No: 9', timezone: 'Europe/Istanbul' });
      const layout = await organizer.post(`/api/organizer/venues/${venue.json.id}/layouts`,
        { name: 'Smoke Plan', sections: [{ name: 'Salon', tierCode: 'A', rows: 2, seatsPerRow: 4 }] });
      const event = await organizer.post(`/api/organizer/organizations/${organizationId}/events`,
        { title: `Smoke Konseri ${suffix} (Demo)`, description: 'Smoke testi için sentetik etkinlik.', category: 'Concert' });
      await organizer.post(`/api/organizer/events/${event.json.id}/publish`);
      // Başlangıç 2 saat sonra: check-in penceresi açık, müşteri iptal sınırı (24 saat) kapalı.
      const session = await organizer.post(`/api/organizer/events/${event.json.id}/sessions`,
        { venueId: venue.json.id, startsAtLocal: istanbulLocal(new Date(Date.now() + 2 * 3600 * 1000)), salesMode: 'ReservedSeating' });
      const seating = await organizer.put(`/api/organizer/sessions/${session.json.id}/seating`, { layoutId: layout.json.id, tierPrices: { A: 45000 } });
      const published = await organizer.post(`/api/organizer/sessions/${session.json.id}/publish`);
      const demo = await organizer.put(`/api/organizer/sessions/${session.json.id}/demo-simulator`, { enabled: false });
      check('Organizatör mekan → plan → etkinlik → seans oluşturup yayınlar',
        venue.status === 201 && layout.status === 201 && event.status === 201 && session.status === 201 && seating.status === 200 && published.status === 200 && demo.status === 200);

      const staff = (await organizer.get(`/api/organizer/sessions/${session.json.id}/gate-staff`)).json;
      const gateUser = staff.find((s) => s.displayName.includes('Kapı'));
      const assigned = await organizer.put(`/api/organizer/sessions/${session.json.id}/gate-staff/${gateUser.userId}`);
      check('Kapı görevlisi seansa atanır', assigned.status === 204);

      Object.assign(state, { organizationId, sessionId: session.json.id });
    },

    async ({ sessions, check, idem }) => {
      const ayse = sessions['customer-ayse@loca.demo'];
      const mehmet = sessions['customer-mehmet@loca.demo'];
      const listed = (await ayse.get('/api/catalog/sessions?limit=50')).json.items.some((i) => i.sessionId === state.sessionId);
      const seats = (await ayse.get(`/api/catalog/sessions/${state.sessionId}/seats`)).json.seats;
      check('Yeni seans public keşifte görünür', listed && seats.length === 8 && seats.every((s) => s.available));

      const key = idem();
      const body = { seatIds: [seats[0].id, seats[1].id] };
      const hold = await ayse.post(`/api/sessions/${state.sessionId}/holds`, body, { headers: { 'idempotency-key': key } });
      const replay = await ayse.post(`/api/sessions/${state.sessionId}/holds`, body, { headers: { 'idempotency-key': key } });
      check('Hold oluşturulur (201) ve süre/garanti alanlarını taşır',
        hold.status === 201 && hold.json.status === 'Active' && hold.json.totalMinor === 90000 && hold.json.guarantee === 'inventory_reserved_until_expiry_not_a_purchase');
      check('Aynı Idempotency-Key: yanıt bayt bayt aynı',
        replay.status === 201 && Buffer.compare(hold.raw, replay.raw) === 0 && replay.headers['idempotent-replayed'] === 'true');
      const conflict = await mehmet.post(`/api/sessions/${state.sessionId}/holds`, { seatIds: [seats[1].id, seats[2].id] }, { headers: { 'idempotency-key': idem() } });
      check('Başkasının tuttuğu koltuk: 409 ve kısmi hold yok', conflict.status === 409 && conflict.json.code === 'seats_unavailable');
      const hint = (await mehmet.get(`/api/catalog/sessions/${state.sessionId}/seats`)).json.seats;
      check('Koltuk planı ipucu güncel (2 dolu, 6 boş)', hint.filter((s) => !s.available).length === 2);

      const checkout = await ayse.post(`/api/holds/${hold.json.id}/checkout`, { paymentMethod: 'tok_success' }, { headers: { 'idempotency-key': idem() } });
      check('Checkout 202 döner (ödeme henüz alınmadı)', checkout.status === 202 && checkout.json.status === 'PendingPayment');
      const paid = await pollOrder(ayse, checkout.json.id, (o) => o.status === 'Paid', 20000);
      check('Ödeme worker ile tamamlanır: sipariş Paid', paid?.status === 'Paid' && paid.payment.status === 'Succeeded');

      let tickets = [];
      for (let i = 0; i < 15 && tickets.length < 2; i++) {
        tickets = (await ayse.get('/api/tickets')).json.filter((t) => t.sessionId === state.sessionId);
        if (tickets.length < 2) await new Promise((r) => setTimeout(r, 1000));
      }
      const detail = await ayse.get(`/api/tickets/${tickets[0]?.id}`);
      trackSecret(detail.json?.token);
      check('Ödenmiş sipariş için 2 bilet üretilir; QR token yalnız ayrıntıda ve no-store',
        tickets.length === 2 && /^LT1\./.test(detail.json?.token ?? '') && detail.headers['cache-control'] === 'no-store' && !JSON.stringify(tickets).includes('token'));
      const stranger = await mehmet.get(`/api/tickets/${tickets[0]?.id}`);
      check('Başka müşteri bileti göremez (404)', stranger.status === 404);

      Object.assign(state, { orderId: checkout.json.id, ticketId: tickets[0]?.id, token: detail.json?.token, freeSeatIds: seats.slice(2).map((s) => s.id) });
    },

    async ({ Client, sessions, check, idem }) => {
      const gate = sessions['gate-kumru@loca.demo'];
      const assigned = (await gate.get('/api/gate/sessions')).json.find((s) => s.sessionId === state.sessionId);
      check('Kapı görevlisi atandığı seansı görür ve pencere açık', Boolean(assigned?.windowOpen));

      const key = idem();
      const first = await gate.post(`/api/gate/sessions/${state.sessionId}/check-ins`, { token: state.token, gate: 'A' }, { headers: { 'idempotency-key': key } });
      const replay = await gate.post(`/api/gate/sessions/${state.sessionId}/check-ins`, { token: state.token, gate: 'A' }, { headers: { 'idempotency-key': key } });
      const second = await gate.post(`/api/gate/sessions/${state.sessionId}/check-ins`, { token: state.token, gate: 'B' }, { headers: { 'idempotency-key': idem() } });
      check('Check-in: ilk tarama Kabul', first.status === 200 && first.json.result === 'Accepted');
      check('Check-in: aynı anahtar aynı yanıt', replay.status === 200 && Buffer.compare(first.raw, replay.raw) === 0);
      check('Check-in: ikinci tarama Zaten Kullanıldı', second.status === 409 && second.json.code === 'already_checked_in' && second.json.gate === 'A');

      const customerScan = await sessions['customer-mehmet@loca.demo'].post(`/api/gate/sessions/${state.sessionId}/check-ins`,
        { token: state.token }, { headers: { 'idempotency-key': idem() } });
      check('Müşteri kapı ucunu kullanamaz (404)', customerScan.status === 404);

      // Simüle PSP uçları yayınlanan HTTPS portundan erişilemez (yalnız süreç içi dinleyici).
      const simulator = await new Client().get('/sim-psp/v1/charges');
      check('Simüle PSP uçları dış porttan erişilemez (404)', simulator.status === 404);
    },

    async ({ sessions, check, idem }) => {
      const organizer = sessions['organizer-kumru@loca.demo'];
      const other = sessions['organizer-marti@loca.demo'];
      const summary = await organizer.get(`/api/organizer/sessions/${state.sessionId}/summary`);
      check('Organizatör özeti: 2 satış, 1 giriş, brüt 900,00 TRY',
        summary.json.sold === 2 && summary.json.ticketsCheckedIn === 1 && summary.json.grossCapturedMinor === 90000 && summary.json.netMinor === 90000);
      const foreign = await other.get(`/api/organizer/sessions/${state.sessionId}/summary`);
      const foreignCancel = await other.post(`/api/organizer/sessions/${state.sessionId}/cancel`, undefined, { headers: { 'idempotency-key': idem() } });
      check('Başka organizatör bu seansı göremez ve iptal edemez (404)', foreign.status === 404 && foreignCancel.status === 404);

      const cancel = await sessions['customer-ayse@loca.demo'].post(`/api/orders/${state.orderId}/cancel`, undefined, { headers: { 'idempotency-key': idem() } });
      check('Kullanılmış biletli / sınırı geçmiş sipariş iptal edilemez (409)', cancel.status === 409);
    },

    async ({ sessions, check, idem }) => {
      // Demo senaryoları yalnız seans opt-in ise kabul edilir.
      const organizer = sessions['organizer-kumru@loca.demo'];
      const mehmet = sessions['customer-mehmet@loca.demo'];
      const hold = await mehmet.post(`/api/sessions/${state.sessionId}/holds`, { seatIds: [state.freeSeatIds[0]] }, { headers: { 'idempotency-key': idem() } });
      const refused = await mehmet.post(`/api/holds/${hold.json.id}/checkout`, { paymentMethod: 'tok_timeout_success' }, { headers: { 'idempotency-key': idem() } });
      check('Demo ödeme senaryosu opt-in olmayan seansta reddedilir (422)', refused.status === 422 && refused.json.code === 'payment_method_not_allowed');

      await organizer.put(`/api/organizer/sessions/${state.sessionId}/demo-simulator`, { enabled: true });
      const checkout = await mehmet.post(`/api/holds/${hold.json.id}/checkout`, { paymentMethod: 'tok_timeout_success' }, { headers: { 'idempotency-key': idem() } });
      const unknown = await pollOrder(mehmet, checkout.json.id, (o) => o.payment.status === 'Unknown' || o.status === 'Paid', 20000);
      check('Sağlayıcı yanıtı kaybolur: ödeme Belirsiz, sipariş beklemede (ret değil)',
        checkout.status === 202 && (unknown?.payment.status === 'Unknown' ? unknown.status === 'PendingPayment' : unknown?.status === 'Paid'));
      const recovered = await pollOrder(mehmet, checkout.json.id, (o) => o.status === 'Paid', 90000);
      check('Reconciliation worker belirsiz ödemeyi kurtarır: Paid', recovered?.status === 'Paid' && recovered.payment.status === 'Succeeded');

      const declinedHold = await sessions['customer-ayse@loca.demo'].post(`/api/sessions/${state.sessionId}/holds`,
        { seatIds: [state.freeSeatIds[1]] }, { headers: { 'idempotency-key': idem() } });
      const declinedCheckout = await sessions['customer-ayse@loca.demo'].post(`/api/holds/${declinedHold.json.id}/checkout`,
        { paymentMethod: 'tok_decline' }, { headers: { 'idempotency-key': idem() } });
      const declined = await pollOrder(sessions['customer-ayse@loca.demo'], declinedCheckout.json.id, (o) => o.status === 'Failed', 20000);
      check('Reddedilen ödeme: sipariş Failed, neden Declined', declined?.status === 'Failed' && declined.payment.failureKind === 'Declined');
      await organizer.put(`/api/organizer/sessions/${state.sessionId}/demo-simulator`, { enabled: false });
      state.recoveredOrderId = checkout.json.id;
    },
  ];
}

/** Stack yeniden başlatıldıktan sonra: veri ve kullanılmış bilet durumu kalıcı mı? */
export function afterRestart() {
  return [
    async ({ Client, check }) => {
      const ayse = new Client();
      await ayse.login('customer-ayse@loca.demo');
      const order = (await ayse.get(`/api/orders/${state.orderId}`)).json;
      const ticket = (await ayse.get(`/api/tickets/${state.ticketId}`)).json;
      check('Restart sonrası sipariş Paid, bilet CheckedIn olarak kalır', order?.status === 'Paid' && ticket?.status === 'CheckedIn' && ticket.token === null);
    },
  ];
}

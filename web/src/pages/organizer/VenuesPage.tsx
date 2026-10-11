import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useUserKey } from '../../auth/AuthContext';
import { EmptyState, ErrorAlert, ErrorState, Loading, OkNote, PageHeading } from '../../components/ui';
import { api, isApiError } from '../../lib/api';
import { invalidFields } from '../../lib/errors';
import type { Layout, LayoutSection, Venue } from '../../lib/types';
import { formatLocalStamp, serverFieldErrors, TextField, useAction } from './shared';

interface VenueDraft {
  name: string;
  city: string;
  address: string;
  timezone: string;
}

type VenueErrors = Partial<Record<keyof VenueDraft, string>>;

function NewVenueForm({ orgId }: { orgId: string }) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<VenueDraft>({ name: '', city: '', address: '', timezone: 'Europe/Istanbul' });
  const [errors, setErrors] = useState<VenueErrors>({});
  const action = useAction();
  const set = (patch: Partial<VenueDraft>) => setDraft({ ...draft, ...patch });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: VenueErrors = {};
    if (draft.name.trim().length < 2 || draft.name.trim().length > 160) next.name = 'Mekan adı 2–160 karakter olmalı.';
    if (draft.city.trim().length < 2 || draft.city.trim().length > 80) next.city = 'Şehir 2–80 karakter olmalı.';
    if (draft.address.trim().length < 2 || draft.address.trim().length > 300) next.address = 'Adres 2–300 karakter olmalı.';
    if (!/^[A-Za-z_]+(\/[A-Za-z0-9_+-]+)+$/.test(draft.timezone.trim()) && draft.timezone.trim() !== 'UTC') {
      next.timezone = 'IANA saat dilimi adı yaz (örnek: Europe/Istanbul).';
    }
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    void action.run('create', async () => {
      try {
        const created = await api.post<Venue>(`/api/organizer/organizations/${orgId}/venues`, {
          name: draft.name.trim(), city: draft.city.trim(), address: draft.address.trim(), timezone: draft.timezone.trim(),
        });
        void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'venues') });
        setDraft({ name: '', city: '', address: '', timezone: draft.timezone.trim() });
        return `“${created.name}” mekanı eklendi.`;
      } catch (error) {
        const mapped = serverFieldErrors<keyof VenueDraft>(error, {
          name: 'Mekan adı geçersiz.', city: 'Şehir geçersiz.', address: 'Adres geçersiz.', timezone: 'Saat dilimi geçersiz.',
        });
        if (isApiError(error, 'invalid_timezone')) mapped.timezone = 'Saat dilimi geçerli bir IANA adı olmalı (örnek: Europe/Istanbul).';
        setErrors(mapped);
        throw error;
      }
    });
  };

  return (
    <form className="form form-wide" onSubmit={submit} noValidate>
      <ErrorAlert error={action.error} />
      {action.done && <OkNote>{action.done}</OkNote>}
      <div className="form-grid">
        <TextField label="Mekan adı" value={draft.name} onChange={(name) => set({ name })} error={errors.name} maxLength={160} />
        <TextField label="Şehir" value={draft.city} onChange={(city) => set({ city })} error={errors.city} maxLength={80} />
        <TextField className="span-2" label="Adres" value={draft.address} onChange={(address) => set({ address })} error={errors.address} maxLength={300} />
        <TextField label="Saat dilimi (IANA)" value={draft.timezone} onChange={(timezone) => set({ timezone })} error={errors.timezone}
          hint="Seans saatleri bu saat diliminde girilir ve gösterilir." autoCapitalize="off" spellCheck={false} />
      </div>
      <div className="row-actions">
        <button type="submit" className="btn btn-primary" disabled={action.busy !== null}>{action.busy ? 'Ekleniyor…' : 'Mekanı ekle'}</button>
      </div>
    </form>
  );
}

interface SectionDraft {
  key: number;
  name: string;
  tierCode: string;
  rows: string;
  seatsPerRow: string;
  aisleAfter: string;
}

type SectionErrors = Partial<Record<'name' | 'tierCode' | 'rows' | 'seatsPerRow' | 'aisleAfter', string>>;

const MAX_SEATS = 2000;
const emptySection = (key: number): SectionDraft => ({ key, name: '', tierCode: '', rows: '', seatsPerRow: '', aisleAfter: '' });
const wholeNumber = (text: string): number | null => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : null);

function validateSections(sections: SectionDraft[]): { errors: SectionErrors[]; total: number; payload: LayoutSection[] } {
  const names = sections.map((s) => s.name.trim().toLocaleLowerCase('tr'));
  let total = 0;
  const payload: LayoutSection[] = [];
  const errors = sections.map((section, index) => {
    const e: SectionErrors = {};
    const name = section.name.trim();
    if (name.length < 1 || name.length > 40) e.name = 'Bölüm adı 1–40 karakter olmalı.';
    else if (names.indexOf(names[index]!) !== index) e.name = 'Bölüm adları birbirinden farklı olmalı.';
    if (!/^[A-Za-z0-9]{1,8}$/.test(section.tierCode.trim())) e.tierCode = 'Kademe kodu 1–8 ASCII harf veya rakam olmalı.';
    const rows = wholeNumber(section.rows);
    if (rows === null || rows < 1 || rows > 40) e.rows = 'Sıra sayısı 1–40 olmalı.';
    const seats = wholeNumber(section.seatsPerRow);
    if (seats === null || seats < 1 || seats > 60) e.seatsPerRow = 'Sıra başına koltuk 1–60 olmalı.';
    let aisle: number | null = null;
    if (section.aisleAfter.trim()) {
      aisle = wholeNumber(section.aisleAfter);
      if (aisle === null || seats === null || aisle < 1 || aisle >= seats) e.aisleAfter = 'Koridor, sıradaki iki koltuğun arasına denk gelmeli.';
    }
    if (rows !== null && seats !== null) total += rows * seats;
    if (Object.keys(e).length === 0 && rows !== null && seats !== null) {
      payload.push({ name, tierCode: section.tierCode.trim(), rows, seatsPerRow: seats, ...(aisle !== null ? { aisleAfter: aisle } : {}) });
    }
    return e;
  });
  return { errors, total, payload };
}

function NewLayoutForm({ venue, onDone }: { venue: Venue; onDone: (message: string) => void }) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [nameError, setNameError] = useState<string | null>(null);
  const [sections, setSections] = useState<SectionDraft[]>([emptySection(1)]);
  const [nextKey, setNextKey] = useState(2);
  const [errors, setErrors] = useState<SectionErrors[]>([]);
  const [formError, setFormError] = useState<string | null>(null);
  const action = useAction();

  const patch = (key: number, change: Partial<SectionDraft>) =>
    setSections(sections.map((s) => (s.key === key ? { ...s, ...change } : s)));

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const checked = validateSections(sections);
    const planName = name.trim();
    const nameProblem = planName.length < 2 || planName.length > 120 ? 'Plan adı 2–120 karakter olmalı.' : null;
    const totalProblem = checked.total > MAX_SEATS ? `Bir plan en fazla ${MAX_SEATS} koltuk içerebilir (şu an ${checked.total}).` : null;
    setNameError(nameProblem);
    setErrors(checked.errors);
    setFormError(totalProblem);
    if (nameProblem || totalProblem || checked.errors.some((e) => Object.keys(e).length > 0)) return;
    void action.run('create', async () => {
      try {
        const created = await api.post<Layout>(`/api/organizer/venues/${venue.id}/layouts`, { name: planName, sections: checked.payload });
        void queryClient.invalidateQueries({ queryKey: userKey('org-venue', venue.id, 'layouts') });
        onDone(`Plan sürümü ${created.version} oluşturuldu (${created.seatCount} koltuk).`);
      } catch (error) {
        // Sunucu alan adları `sections[0].tierCode` biçimindedir; ilgili satıra genel bir Türkçe mesaj yansıtılır.
        const fields = invalidFields(error);
        if (fields.includes('name')) setNameError('Plan adı geçersiz.');
        setErrors(sections.map((_, index) => {
          const e: SectionErrors = {};
          for (const field of ['name', 'tierCode', 'rows', 'seatsPerRow', 'aisleAfter'] as const) {
            if (fields.includes(`sections[${index}].${field}`)) e[field] = 'Sunucu bu değeri kabul etmedi.';
          }
          return e;
        }));
        if (fields.includes('sections')) setFormError('Bölümler geçersiz: adlar farklı olmalı ve toplam koltuk sayısı sınırı aşmamalı.');
        throw error;
      }
    });
  };

  return (
    <form className="form form-wide box" onSubmit={submit} noValidate>
      <ErrorAlert error={action.error ?? formError} />
      <TextField label="Plan adı" value={name} onChange={setName} error={nameError} maxLength={120} />
      <fieldset>
        <legend className="lbl">Bölümler</legend>
        <p className="note">Her bölüm tek bir fiyat kademesine bağlıdır; fiyatlar seans bazında girilir. Sıralar A, B, C… olarak adlandırılır.</p>
        <div className="sections" style={{ marginTop: 8 }}>
          {sections.map((section, index) => {
            const e = errors[index] ?? {};
            const n = index + 1;
            return (
              <div className="section-row" key={section.key} role="group" aria-label={`Bölüm ${n}`}>
                <TextField className="span-2" label={`Bölüm ${n} adı`} value={section.name} onChange={(v) => patch(section.key, { name: v })} error={e.name} maxLength={40} />
                <TextField label="Kademe kodu" value={section.tierCode} onChange={(v) => patch(section.key, { tierCode: v })} error={e.tierCode}
                  maxLength={8} autoCapitalize="characters" spellCheck={false} />
                <TextField label="Sıra sayısı" inputMode="numeric" value={section.rows} onChange={(v) => patch(section.key, { rows: v })} error={e.rows} />
                <TextField label="Sıra başına koltuk" inputMode="numeric" value={section.seatsPerRow} onChange={(v) => patch(section.key, { seatsPerRow: v })} error={e.seatsPerRow} />
                <TextField label="Koridor (koltuktan sonra)" inputMode="numeric" value={section.aisleAfter} onChange={(v) => patch(section.key, { aisleAfter: v })}
                  error={e.aisleAfter} hint="İsteğe bağlı" />
                <button type="button" className="btn btn-sm" disabled={sections.length === 1}
                  onClick={() => { setSections(sections.filter((s) => s.key !== section.key)); setErrors([]); }}>
                  Bölümü çıkar<span className="sr-only"> ({n})</span>
                </button>
              </div>
            );
          })}
        </div>
        <p style={{ marginTop: 10 }}>
          <button type="button" className="btn btn-sm" disabled={sections.length >= 10}
            onClick={() => { setSections([...sections, emptySection(nextKey)]); setNextKey(nextKey + 1); }}>
            Bölüm ekle
          </button>
        </p>
      </fieldset>
      <div className="row-actions">
        <button type="submit" className="btn btn-primary" disabled={action.busy !== null}>
          {action.busy ? 'Plan oluşturuluyor…' : 'Plan sürümünü oluştur'}
        </button>
      </div>
    </form>
  );
}

function VenueLayouts({ venue }: { venue: Venue }) {
  const userKey = useUserKey();
  const [adding, setAdding] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const layouts = useQuery({
    queryKey: userKey('org-venue', venue.id, 'layouts'),
    queryFn: () => api.get<Layout[]>(`/api/organizer/venues/${venue.id}/layouts`),
  });

  return (
    <div className="sub">
      <h3>Koltuk planı sürümleri</h3>
      {layouts.isPending ? <Loading label="Planlar yükleniyor…" />
        : layouts.isError ? <ErrorState error={layouts.error} onRetry={() => void layouts.refetch()} />
          : layouts.data.length === 0 ? <p className="note">Bu mekan için henüz plan yok. Koltuklu seans için en az bir plan gerekir.</p>
            : (
              <ul className="org-sessions" data-testid="venue-layouts">
                {layouts.data.map((layout) => (
                  <li key={layout.id}>
                    <strong className="mono">v{layout.version}</strong>
                    <span className="break">{layout.name}</span>
                    <span>{layout.seatCount} koltuk</span>
                    <span className="t-mute">Kademeler: <span className="mono">{layout.tierCodes.join(', ')}</span></span>
                    <span className="t-mute">{formatLocalStamp(layout.createdAt)}</span>
                  </li>
                ))}
              </ul>
            )}
      {done && <OkNote>{done}</OkNote>}
      {adding ? (
        <NewLayoutForm venue={venue} onDone={(message) => { setDone(message); setAdding(false); }} />
      ) : (
        <p style={{ marginTop: 10 }}>
          <button type="button" className="btn btn-sm" onClick={() => { setAdding(true); setDone(null); }}>
            Yeni plan sürümü<span className="sr-only"> — {venue.name}</span>
          </button>
        </p>
      )}
    </div>
  );
}

export function VenuesPage() {
  const { orgId = '' } = useParams();
  const userKey = useUserKey();
  const venues = useQuery({
    queryKey: userKey('org', orgId, 'venues'),
    queryFn: () => api.get<Venue[]>(`/api/organizer/organizations/${orgId}/venues`),
  });

  return (
    <>
      <PageHeading title="Mekanlar" />
      <p className="lede">
        Koltuk planları değişmezdir: bir plan düzenlenmez, her kayıt yeni bir sürüm oluşturur. Seanslar seçtikleri sürümü kullanmaya devam eder.
      </p>
      {venues.isPending ? <Loading label="Mekanlar yükleniyor…" />
        : venues.isError ? <ErrorState error={venues.error} onRetry={() => void venues.refetch()} />
          : venues.data.length === 0 ? <EmptyState title="Henüz mekan yok."><p>İlk mekanı aşağıdaki formla ekle.</p></EmptyState>
            : venues.data.map((venue) => (
              <div className="blk" key={venue.id} data-testid="venue">
                <h2>{venue.name}</h2>
                <p className="note">{venue.city} · {venue.address} · <span className="mono">{venue.timezone}</span></p>
                <VenueLayouts venue={venue} />
              </div>
            ))}

      <div className="blk">
        <h2>Yeni mekan</h2>
        <p className="note">Seansın şehri ve saat dilimi mekandan gelir.</p>
        <NewVenueForm orgId={orgId} />
      </div>
    </>
  );
}

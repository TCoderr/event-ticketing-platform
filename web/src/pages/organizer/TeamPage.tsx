import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useAuth, useUserKey } from '../../auth/AuthContext';
import { ConfirmPanel, EmptyState, ErrorAlert, ErrorState, Loading, OkNote, PageHeading } from '../../components/ui';
import { api, isApiError } from '../../lib/api';
import { formatSessionTime } from '../../lib/format';
import type { Member, MemberRole, OrganizerEvent } from '../../lib/types';
import { GateStaffAssign } from './GateStaffAssign';
import { SelectField, serverFieldErrors, TextField, useAction } from './shared';

const ROLE_LABELS: Record<MemberRole, string> = { OrganizerAdmin: 'Organizasyon yöneticisi', GateStaff: 'Kapı görevlisi' };

function AddMemberForm({ orgId }: { orgId: string }) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const { user, reload } = useAuth();
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<MemberRole>('GateStaff');
  const [emailError, setEmailError] = useState<string | null>(null);
  const action = useAction();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const value = email.trim();
    if (value.length < 5 || !value.includes('@')) {
      setEmailError('Geçerli bir e-posta adresi yaz.');
      return;
    }
    setEmailError(null);
    void action.run('add', async () => {
      try {
        const member = await api.post<Member>(`/api/organizer/organizations/${orgId}/members`, { email: value, role });
        void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'members') });
        void queryClient.invalidateQueries({ queryKey: userKey('org-session') });
        if (member.userId === user?.id) await reload();
        setEmail('');
        return `${member.displayName} ${ROLE_LABELS[member.role].toLocaleLowerCase('tr')} olarak kaydedildi.`;
      } catch (error) {
        if (isApiError(error, 'user_not_found')) setEmailError('Bu e-posta adresiyle kayıtlı kullanıcı yok. Kişi önce Loca hesabı açmalı.');
        else setEmailError(serverFieldErrors(error, { email: 'E-posta adresi geçersiz.' }).email ?? null);
        throw error;
      }
    });
  };

  return (
    <form className="form form-wide" onSubmit={submit} noValidate>
      {/* user_not_found alan hatası olarak gösterilir; diğer hatalar burada duyurulur. */}
      <ErrorAlert error={action.error !== null && emailError === null ? action.error : null} />
      {action.done && <OkNote>{action.done}</OkNote>}
      <div className="form-grid">
        <TextField label="E-posta" type="email" value={email} onChange={setEmail} error={emailError} autoComplete="off"
          hint="Kişinin kayıtlı bir Loca hesabı olmalı. Mevcut üyenin rolü de bu formla değiştirilir." />
        <SelectField label="Rol" value={role} onChange={(value) => setRole(value as MemberRole)}>
          <option value="GateStaff">{ROLE_LABELS.GateStaff}</option>
          <option value="OrganizerAdmin">{ROLE_LABELS.OrganizerAdmin}</option>
        </SelectField>
      </div>
      <div className="row-actions">
        <button type="submit" className="btn btn-primary" disabled={action.busy !== null}>{action.busy ? 'Ekleniyor…' : 'Üye ekle'}</button>
      </div>
    </form>
  );
}

function MemberList({ orgId }: { orgId: string }) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const { user, reload } = useAuth();
  const members = useQuery({
    queryKey: userKey('org', orgId, 'members'),
    queryFn: () => api.get<Member[]>(`/api/organizer/organizations/${orgId}/members`),
  });
  const [confirming, setConfirming] = useState<string | null>(null);
  const action = useAction();

  const remove = (member: Member) =>
    void action.run(member.userId, async () => {
      await api.delete(`/api/organizer/organizations/${orgId}/members/${member.userId}`);
      setConfirming(null);
      void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'members') });
      void queryClient.invalidateQueries({ queryKey: userKey('org-session') });
      if (member.userId === user?.id) await reload(); // Kendi üyeliğim bitti: gezinme ve yetki yeniden okunur.
      return `${member.displayName} organizasyondan çıkarıldı.`;
    }).then((ok) => {
      if (!ok) setConfirming(null);
    });

  if (members.isPending) return <Loading label="Üyeler yükleniyor…" />;
  if (members.isError) return <ErrorState error={members.error} onRetry={() => void members.refetch()} />;
  if (members.data.length === 0) return <EmptyState title="Organizasyonda üye yok." />;

  return (
    <>
      <ErrorAlert error={action.error} />
      {action.done && <OkNote>{action.done}</OkNote>}
      <ul className="list" data-testid="org-members">
        {members.data.map((member) => (
          <li key={member.userId}>
            <div>
              <p style={{ fontWeight: 600 }} className="break">{member.displayName}{member.userId === user?.id ? ' (sen)' : ''}</p>
              <p className="l-meta break">{member.email}</p>
            </div>
            <div className="l-side">
              <span className="chip">{ROLE_LABELS[member.role]}</span>
              {confirming !== member.userId && (
                <button type="button" className="btn btn-sm" onClick={() => { setConfirming(member.userId); action.reset(); }} disabled={action.busy !== null}>
                  Üyeyi çıkar<span className="sr-only"> — {member.displayName}</span>
                </button>
              )}
            </div>
            {confirming === member.userId && (
              <div className="span-all">
                <ConfirmPanel title="Üye çıkarılsın mı?" confirmLabel="Evet, üyeyi çıkar" onConfirm={() => remove(member)}
                  onCancel={() => setConfirming(null)} busy={action.busy !== null}>
                  <p>{member.displayName} bu organizasyondaki rolünü ve kapı görevlerini kaybeder. Hesabı silinmez.</p>
                </ConfirmPanel>
              </div>
            )}
          </li>
        ))}
      </ul>
    </>
  );
}

function SessionStaffPicker({ orgId }: { orgId: string }) {
  const userKey = useUserKey();
  const events = useQuery({
    queryKey: userKey('org', orgId, 'events'),
    queryFn: () => api.get<OrganizerEvent[]>(`/api/organizer/organizations/${orgId}/events`),
  });
  const [chosen, setChosen] = useState('');

  if (events.isPending) return <Loading label="Seanslar yükleniyor…" />;
  if (events.isError) return <ErrorState error={events.error} onRetry={() => void events.refetch()} />;
  const sessions = events.data.flatMap((event) =>
    event.sessions.filter((s) => s.status !== 'Cancelled').map((s) => ({ ...s, eventTitle: event.title })));
  if (sessions.length === 0) return <p className="note">Atama yapılabilecek seans yok. Önce bir etkinliğe seans ekle.</p>;
  const sessionId = sessions.some((s) => s.id === chosen) ? chosen : sessions[0]!.id;

  return (
    <>
      <div className="form">
        <SelectField label="Seans" value={sessionId} onChange={setChosen}>
          {sessions.map((s) => (
            <option key={s.id} value={s.id}>{s.eventTitle} — {formatSessionTime(s.startsAt, s.timezone)}</option>
          ))}
        </SelectField>
      </div>
      <GateStaffAssign key={sessionId} sessionId={sessionId} />
    </>
  );
}

export function TeamPage() {
  const { orgId = '' } = useParams();
  return (
    <>
      <PageHeading title="Ekip" />
      <p className="lede">Organizasyon yöneticileri etkinlikleri yönetir; kapı görevlileri yalnız atandıkları seanslarda bilet okutabilir.</p>

      <div className="blk">
        <h2>Üyeler</h2>
        <MemberList orgId={orgId} />
        <div className="sub">
          <h3>Üye ekle</h3>
          <AddMemberForm orgId={orgId} />
        </div>
      </div>

      <div className="blk">
        <h2>Seansa kapı görevlisi ata</h2>
        <p className="note">Bir üyenin kapıda görev yapabilmesi için seansa atanması gerekir.</p>
        <SessionStaffPicker orgId={orgId} />
      </div>
    </>
  );
}

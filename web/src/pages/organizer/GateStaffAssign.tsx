import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useUserKey } from '../../auth/AuthContext';
import { ErrorAlert, ErrorState, Loading, Status } from '../../components/ui';
import { api } from '../../lib/api';
import type { GateStaff } from '../../lib/types';
import { useAction } from './shared';

/** Seansın kapı ekibi: organizasyon üyeleri atanmış/atanmamış olarak listelenir; ata / çıkar. */
export function GateStaffAssign({ sessionId, disabled = false }: { sessionId: string; disabled?: boolean }) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const key = userKey('org-session', sessionId, 'gate-staff');
  const staff = useQuery({ queryKey: key, queryFn: () => api.get<GateStaff[]>(`/api/organizer/sessions/${sessionId}/gate-staff`) });
  const action = useAction();

  const toggle = (member: GateStaff) =>
    void action.run(member.userId, async () => {
      const path = `/api/organizer/sessions/${sessionId}/gate-staff/${member.userId}`;
      if (member.assigned) await api.delete(path);
      else await api.put(path);
      await queryClient.invalidateQueries({ queryKey: key });
    });

  if (staff.isPending) return <Loading label="Kapı ekibi yükleniyor…" />;
  if (staff.isError) return <ErrorState error={staff.error} onRetry={() => void staff.refetch()} />;
  if (staff.data.length === 0) return <p className="note">Organizasyonda üye yok. Önce Ekip sayfasından üye ekle.</p>;

  return (
    <>
      <ErrorAlert error={action.error} />
      <ul className="list" data-testid="gate-staff">
        {staff.data.map((member) => (
          <li key={member.userId} data-assigned={member.assigned}>
            <div>
              <p style={{ fontWeight: 600 }} className="break">{member.displayName}</p>
            </div>
            <div className="l-side">
              {member.assigned
                ? <Status tone="ok" icon="check">Bu seansa atandı</Status>
                : <Status tone="mute" icon="planned">Atanmadı</Status>}
              <button type="button" className="btn btn-sm" onClick={() => toggle(member)} disabled={disabled || action.busy !== null}>
                {action.busy === member.userId ? 'Kaydediliyor…' : member.assigned ? 'Görevden çıkar' : 'Seansa ata'}
                <span className="sr-only"> — {member.displayName}</span>
              </button>
            </div>
          </li>
        ))}
      </ul>
    </>
  );
}

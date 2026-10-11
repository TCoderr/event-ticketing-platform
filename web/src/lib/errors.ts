import { ApiError, NetworkError, type Problem } from './api';

type Message = string | ((problem: Problem) => string);

// Hata kodu → kullanıcı metni. Sunucunun İngilizce `detail` alanı kullanıcıya gösterilmez.
const MESSAGES: Record<string, Message> = {
  validation_failed: 'Bazı alanlar geçersiz. İşaretli alanları düzeltip yeniden dene.',
  unknown_field: 'İstek sunucunun tanımadığı bir alan içeriyor. Sayfayı yenileyip yeniden dene.',
  unauthenticated: 'Oturumun sona erdi. Devam etmek için yeniden giriş yap.',
  forbidden: 'Bu işlem için yetkin yok.',
  not_found: 'Aradığın kayıt bulunamadı ya da buna erişimin yok.',
  csrf_invalid: 'Güvenlik doğrulaması yenilenemedi. Sayfayı yenileyip yeniden dene.',
  rate_limited: (p) => {
    const seconds = typeof p.retryAfterSeconds === 'number' ? p.retryAfterSeconds : null;
    return seconds === null
      ? 'Çok fazla istek gönderildi. Biraz bekleyip yeniden dene.'
      : `Çok fazla istek gönderildi. ${seconds} saniye sonra yeniden dene.`;
  },
  conflict: 'İşlem mevcut durumla çakışıyor. Sayfayı yenileyip yeniden dene.',
  internal_error: 'Beklenmeyen bir hata oluştu. İşlem tamamlanmamış olabilir; yeniden dene.',
  service_unavailable: 'Sunucuya şu anda ulaşılamıyor. İşlemin sonucu bilinmiyor; yeniden dene.',
  payload_too_large: 'Gönderilen veri çok büyük.',
  idempotency_key_required: 'İstek anahtarı eksik. Sayfayı yenileyip yeniden dene.',
  idempotency_key_invalid: 'İstek anahtarı geçersiz. Sayfayı yenileyip yeniden dene.',
  idempotency_key_reused: 'Bu istek anahtarı başka bir işlem için kullanılmış. Sayfayı yenileyip yeniden dene.',
  request_in_progress: 'Aynı işlem hâlâ sürüyor. Birkaç saniye sonra yeniden dene.',

  invalid_credentials: 'E-posta veya parola hatalı.',
  email_already_registered: 'Bu e-posta adresiyle kayıtlı bir hesap var. Giriş yapmayı dene.',
  weak_password: 'Parola kurallara uymuyor: en az 10 karakter olmalı, en az bir küçük harf ve bir rakam içermeli.',
  user_not_found: 'Bu e-posta adresiyle kayıtlı kullanıcı yok ya da kullanıcı bu organizasyonun üyesi değil.',
  last_admin_cannot_leave: 'Organizasyonda en az bir yönetici kalmalı.',

  unsupported_currency: 'Yalnız TRY desteklenir.',
  invalid_timezone: 'Saat dilimi geçerli bir IANA adı olmalı (örnek: Europe/Istanbul).',
  invalid_local_time: 'Bu yerel saat, mekanın saat diliminde yok ya da iki kez yaşanıyor (yaz/kış saati geçişi). Başka bir saat seç.',
  organization_not_active: 'Organizasyon henüz onaylanmadı ya da askıya alındı; yayın yapılamaz.',
  publish_guard_failed: (p) => {
    const reasons = Array.isArray(p.reasons) ? p.reasons.filter((r): r is string => typeof r === 'string') : [];
    if (reasons.length === 0) return 'Yayın koşulları sağlanmıyor.';
    return `Yayınlanamıyor: ${reasons.map((r) => PUBLISH_REASONS[r] ?? r).join('; ')}.`;
  },
  invalid_state_transition: 'Kayıt şu anki durumunda bu işleme izin vermiyor.',
  layout_locked: 'Bu seans için rezervasyon oluştuğundan koltuk planı artık değiştirilemez.',
  price_locked: 'Tutulmuş veya satılmış koltuğu olan kademenin fiyatı değiştirilemez.',
  capacity_below_committed: (p) =>
    typeof p.committed === 'number'
      ? `Kapasite, tutulmuş ve satılmış bilet sayısının (${p.committed}) altına indirilemez.`
      : 'Kapasite, tutulmuş ve satılmış bilet sayısının altına indirilemez.',
  session_not_editable: 'Yalnız taslak seanslar düzenlenebilir.',
  moderation_blocked: 'Bu etkinlik platform yönetimi tarafından yayından kaldırıldı.',

  session_not_on_sale: 'Bu seans şu anda satışta değil.',
  sales_not_started: 'Bu seansın satışı henüz başlamadı.',
  sales_closed: 'Bu seansın satışı kapandı.',
  invalid_selection: 'Seçim geçersiz. Tek seferde 1–6 bilet seçilebilir.',
  seats_unavailable: 'Seçtiğin koltuklardan en az biri az önce başkası tarafından alındı. Hiçbir koltuk tutulmadı.',
  insufficient_capacity: 'Seçtiğin adet kadar bilet kalmadı. Hiçbir bilet tutulmadı.',
  active_hold_exists: 'Bu seansta zaten süresi dolmamış bir rezervasyonun var.',
  hold_expired: 'Rezervasyon süresi doldu; seçimin artık ayrılmış değil.',
  hold_not_active: 'Bu rezervasyon artık aktif değil.',

  order_already_exists: 'Bu rezervasyon için ödeme zaten başlatılmış.',
  payment_method_not_allowed: 'Bu ödeme senaryosu bu seansta kullanılamıyor.',
  order_not_paid: 'Yalnız ödenmiş siparişler iptal edilebilir.',
  cancellation_window_closed: 'İptal süresi doldu; bu sipariş artık iptal edilemez.',
  order_has_checked_in_ticket: 'Bileti kullanılmış bir sipariş iptal edilemez.',
  refund_not_retryable: 'Yalnız başarısız iadeler yeniden denenebilir.',
  payment_not_reconcilable: 'Bu ödeme için mutabakat yapılamaz.',

  ticket_not_found: 'Bu kodla bilet bulunamadı.',
  wrong_session: 'Bilet bu seansa ait değil.',
  ticket_void: 'Bilet iptal veya iade edilmiş.',
  already_checked_in: 'Bu bilet daha önce kullanıldı.',
  outside_checkin_window: 'Giriş penceresi dışında.',
  session_cancelled: 'Bu seans iptal edildi.',
};

const PUBLISH_REASONS: Record<string, string> = {
  organization_not_active: 'organizasyon aktif değil',
  event_not_published: 'etkinlik henüz yayında değil',
  event_taken_down: 'etkinlik yayından kaldırılmış',
  session_start_in_past: 'seans başlangıcı geçmişte',
  invalid_sales_window: 'satış penceresi geçersiz',
  seating_not_configured: 'koltuk planı ve fiyatlar tanımlanmamış',
  no_ticket_category: 'en az bir bilet kategorisi gerekli',
};

export const NETWORK_MESSAGE = 'Sunucuya ulaşılamadı. İşlemin sonucu bilinmiyor; bağlantını kontrol edip yeniden dene.';

/** Herhangi bir hatayı kullanıcıya gösterilecek Türkçe metne çevirir. */
export function describeError(error: unknown): string {
  if (error instanceof NetworkError) return NETWORK_MESSAGE;
  if (error instanceof ApiError) {
    const message = MESSAGES[error.code];
    if (typeof message === 'function') return message(error.problem);
    if (message) return message;
    if (error.status >= 500) return MESSAGES.internal_error as string;
    return `İşlem tamamlanamadı (${error.code}).`;
  }
  return 'Beklenmeyen bir hata oluştu.';
}

/** `validation_failed` yanıtındaki alan adları (`errors` sözlüğünün anahtarları). */
export function invalidFields(error: unknown): string[] {
  if (!(error instanceof ApiError)) return [];
  const errors = error.problem.errors;
  if (errors && typeof errors === 'object') return Object.keys(errors);
  return typeof error.problem.field === 'string' ? [error.problem.field] : [];
}

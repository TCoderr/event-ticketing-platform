namespace Loca.Domain;

/// <summary>ProblemDetails <c>code</c> uzantısında dönen, makinece okunur hata kodları.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string UnknownField = "unknown_field";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string CsrfInvalid = "csrf_invalid";
    public const string RateLimited = "rate_limited";
    public const string Conflict = "conflict";
    public const string InternalError = "internal_error";
    public const string ServiceUnavailable = "service_unavailable";
    public const string PayloadTooLarge = "payload_too_large";

    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string RequestInProgress = "request_in_progress";

    public const string InvalidCredentials = "invalid_credentials";
    public const string EmailAlreadyRegistered = "email_already_registered";
    public const string WeakPassword = "weak_password";
    public const string UserNotFound = "user_not_found";
    public const string LastAdminCannotLeave = "last_admin_cannot_leave";

    public const string UnsupportedCurrency = "unsupported_currency";
    public const string InvalidTimezone = "invalid_timezone";
    public const string InvalidLocalTime = "invalid_local_time";
    public const string OrganizationNotActive = "organization_not_active";
    public const string PublishGuardFailed = "publish_guard_failed";
    public const string InvalidStateTransition = "invalid_state_transition";
    public const string LayoutLocked = "layout_locked";
    public const string PriceLocked = "price_locked";
    public const string CapacityBelowCommitted = "capacity_below_committed";
    public const string SessionNotEditable = "session_not_editable";
    public const string ModerationBlocked = "moderation_blocked";

    public const string SessionNotOnSale = "session_not_on_sale";
    public const string SalesNotStarted = "sales_not_started";
    public const string SalesClosed = "sales_closed";
    public const string InvalidSelection = "invalid_selection";
    public const string SeatsUnavailable = "seats_unavailable";
    public const string InsufficientCapacity = "insufficient_capacity";
    public const string ActiveHoldExists = "active_hold_exists";
    public const string HoldExpired = "hold_expired";
    public const string HoldNotActive = "hold_not_active";

    public const string OrderAlreadyExists = "order_already_exists";
    public const string PaymentMethodNotAllowed = "payment_method_not_allowed";
    public const string OrderNotPaid = "order_not_paid";
    public const string CancellationWindowClosed = "cancellation_window_closed";
    public const string OrderHasCheckedInTicket = "order_has_checked_in_ticket";
    public const string RefundNotRetryable = "refund_not_retryable";
    public const string PaymentNotReconcilable = "payment_not_reconcilable";

    public const string TicketNotFound = "ticket_not_found";
    public const string WrongSession = "wrong_session";
    public const string TicketVoid = "ticket_void";
    public const string AlreadyCheckedIn = "already_checked_in";
    public const string OutsideCheckInWindow = "outside_checkin_window";
    public const string SessionCancelled = "session_cancelled";

    public const string WebhookSignatureInvalid = "webhook_signature_invalid";
    public const string WebhookTimestampOutOfRange = "webhook_timestamp_out_of_range";
    public const string WebhookEventConflict = "webhook_event_conflict";
    public const string WebhookMalformed = "webhook_malformed";
}

/// <summary>Saf domain kuralı ihlali. HTTP durum kodu taşımaz; eşleme uygulama katmanındadır.</summary>
public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

namespace Loca.Domain;

/// <summary>
/// Durum geçiş tabloları. Buradaki her geçiş docs/02-domain-and-state-machines.md ile birebir eşleşir.
/// </summary>
public static class StateMachines
{
    public static bool CanTransition(ReservationStatus from, ReservationStatus to) => (from, to) switch
    {
        (ReservationStatus.Active, ReservationStatus.Consumed) => true,
        (ReservationStatus.Active, ReservationStatus.Expired) => true,
        (ReservationStatus.Active, ReservationStatus.Released) => true,
        _ => false,
    };

    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.PendingPayment, OrderStatus.Paid) => true,
        (OrderStatus.PendingPayment, OrderStatus.Failed) => true,
        (OrderStatus.PendingPayment, OrderStatus.Expired) => true,
        (OrderStatus.PendingPayment, OrderStatus.Cancelled) => true,
        (OrderStatus.Paid, OrderStatus.Cancelled) => true,
        _ => false,
    };

    public static bool IsTerminal(PaymentStatus status) =>
        status is PaymentStatus.Succeeded or PaymentStatus.Declined or PaymentStatus.Failed;

    public static bool CanTransition(PaymentStatus from, PaymentStatus to)
    {
        if (from == to || IsTerminal(from)) return false;
        return (from, to) switch
        {
            (_, PaymentStatus.Initiated) => false,
            (PaymentStatus.Initiated, _) => true,
            (PaymentStatus.Pending, PaymentStatus.Succeeded or PaymentStatus.Declined or PaymentStatus.Failed or PaymentStatus.Unknown) => true,
            (PaymentStatus.Unknown, PaymentStatus.Succeeded or PaymentStatus.Declined or PaymentStatus.Failed or PaymentStatus.Pending) => true,
            _ => false,
        };
    }

    public static bool CanTransition(RefundStatus from, RefundStatus to) => (from, to) switch
    {
        (RefundStatus.Pending, RefundStatus.Succeeded) => true,
        (RefundStatus.Pending, RefundStatus.Failed) => true,
        (RefundStatus.Failed, RefundStatus.Pending) => true,
        _ => false,
    };

    /// <param name="sessionCancelled">CheckedIn → Cancelled yalnız seans iptalinde mümkündür.</param>
    public static bool CanTransition(TicketStatus from, TicketStatus to, bool sessionCancelled = false) => (from, to) switch
    {
        (TicketStatus.Valid, TicketStatus.CheckedIn) => true,
        (TicketStatus.Valid, TicketStatus.Cancelled) => true,
        (TicketStatus.CheckedIn, TicketStatus.Cancelled) => sessionCancelled,
        _ => false,
    };

    public static bool CanTransition(SessionStatus from, SessionStatus to) => (from, to) switch
    {
        (SessionStatus.Draft, SessionStatus.Published) => true,
        (SessionStatus.Draft, SessionStatus.Cancelled) => true,
        (SessionStatus.Published, SessionStatus.Cancelled) => true,
        _ => false,
    };

    public static bool CanTransition(EventStatus from, EventStatus to) => (from, to) switch
    {
        (EventStatus.Draft, EventStatus.Published) => true,
        (EventStatus.Published, EventStatus.Draft) => true,
        (EventStatus.Draft, EventStatus.Cancelled) => true,
        (EventStatus.Published, EventStatus.Cancelled) => true,
        _ => false,
    };

    public static bool CanTransition(OrganizationStatus from, OrganizationStatus to) => (from, to) switch
    {
        (OrganizationStatus.PendingApproval, OrganizationStatus.Active) => true,
        (OrganizationStatus.Active, OrganizationStatus.Suspended) => true,
        (OrganizationStatus.Suspended, OrganizationStatus.Active) => true,
        _ => false,
    };

    public static void Ensure(bool allowed, object from, object to)
    {
        if (!allowed)
            throw new DomainRuleException(ErrorCodes.InvalidStateTransition, $"Transition {from} -> {to} is not allowed.");
    }
}

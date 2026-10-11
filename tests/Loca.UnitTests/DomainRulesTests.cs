using Loca.Domain;

namespace Loca.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void Only_try_is_supported()
    {
        Assert.Equal(45000, Money.FromMinor(45000, "TRY").Minor);
        foreach (var currency in new[] { "USD", "EUR", "try", "", "TL" })
        {
            var error = Assert.Throws<DomainRuleException>(() => Money.FromMinor(100, currency));
            Assert.Equal(ErrorCodes.UnsupportedCurrency, error.Code);
        }
    }

    [Fact]
    public void Negative_and_oversized_amounts_are_rejected()
    {
        Assert.Throws<DomainRuleException>(() => Money.FromMinor(-1));
        Assert.Throws<DomainRuleException>(() => Money.FromMinor(Money.MaxMinor + 1));
        Assert.True(Money.IsValidPriceMinor(0));
        Assert.True(Money.IsValidPriceMinor(Money.MaxMinor));
        Assert.False(Money.IsValidPriceMinor(-1));
    }

    [Fact]
    public void Arithmetic_stays_in_integer_minor_units()
    {
        var line = Money.FromMinor(33333).Multiply(3);
        Assert.Equal(99999, line.Minor);
        Assert.Equal(100000, line.Add(Money.FromMinor(1)).Minor);
        Assert.Equal("1000.00 TRY", Money.FromMinor(100000).ToString());
    }
}

public sealed class StateMachineTests
{
    [Fact]
    public void Reservation_only_leaves_active_and_terminal_states_never_change()
    {
        foreach (var from in Enum.GetValues<ReservationStatus>())
        foreach (var to in Enum.GetValues<ReservationStatus>())
        {
            var expected = from == ReservationStatus.Active && to != ReservationStatus.Active;
            Assert.Equal(expected, StateMachines.CanTransition(from, to));
        }
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Paid, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Failed, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Expired, true)]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Paid, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Expired, OrderStatus.Paid, false)]   // geç başarı siparişi yeniden Paid yapmaz
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid, false)]
    [InlineData(OrderStatus.Failed, OrderStatus.Paid, false)]
    [InlineData(OrderStatus.Paid, OrderStatus.Expired, false)]   // ödenmiş sipariş süre dolumuyla kapanmaz
    [InlineData(OrderStatus.Paid, OrderStatus.PendingPayment, false)]
    public void Order_transitions(OrderStatus from, OrderStatus to, bool allowed) =>
        Assert.Equal(allowed, StateMachines.CanTransition(from, to));

    [Fact]
    public void Payment_terminal_states_are_final_and_unknown_is_not_a_decline()
    {
        foreach (var terminal in new[] { PaymentStatus.Succeeded, PaymentStatus.Declined, PaymentStatus.Failed })
        {
            Assert.True(StateMachines.IsTerminal(terminal));
            foreach (var to in Enum.GetValues<PaymentStatus>())
                Assert.False(StateMachines.CanTransition(terminal, to));
        }

        Assert.False(StateMachines.IsTerminal(PaymentStatus.Unknown));
        Assert.True(StateMachines.CanTransition(PaymentStatus.Unknown, PaymentStatus.Succeeded));
        Assert.True(StateMachines.CanTransition(PaymentStatus.Unknown, PaymentStatus.Declined));
        Assert.True(StateMachines.CanTransition(PaymentStatus.Initiated, PaymentStatus.Unknown));
        Assert.True(StateMachines.CanTransition(PaymentStatus.Pending, PaymentStatus.Succeeded));
        Assert.False(StateMachines.CanTransition(PaymentStatus.Pending, PaymentStatus.Initiated));
        Assert.False(StateMachines.CanTransition(PaymentStatus.Pending, PaymentStatus.Pending));
    }

    [Fact]
    public void Refund_can_only_be_retried_from_failed()
    {
        Assert.True(StateMachines.CanTransition(RefundStatus.Pending, RefundStatus.Succeeded));
        Assert.True(StateMachines.CanTransition(RefundStatus.Pending, RefundStatus.Failed));
        Assert.True(StateMachines.CanTransition(RefundStatus.Failed, RefundStatus.Pending));
        Assert.False(StateMachines.CanTransition(RefundStatus.Succeeded, RefundStatus.Pending));
        Assert.False(StateMachines.CanTransition(RefundStatus.Succeeded, RefundStatus.Failed));
    }

    [Fact]
    public void Checked_in_ticket_is_only_voided_by_session_cancellation()
    {
        Assert.True(StateMachines.CanTransition(TicketStatus.Valid, TicketStatus.CheckedIn));
        Assert.True(StateMachines.CanTransition(TicketStatus.Valid, TicketStatus.Cancelled));
        Assert.False(StateMachines.CanTransition(TicketStatus.CheckedIn, TicketStatus.Cancelled));
        Assert.True(StateMachines.CanTransition(TicketStatus.CheckedIn, TicketStatus.Cancelled, sessionCancelled: true));
        Assert.False(StateMachines.CanTransition(TicketStatus.CheckedIn, TicketStatus.CheckedIn));
        Assert.False(StateMachines.CanTransition(TicketStatus.Cancelled, TicketStatus.Valid));
        Assert.False(StateMachines.CanTransition(TicketStatus.Cancelled, TicketStatus.CheckedIn));
    }

    [Fact]
    public void Cancelled_session_never_reopens()
    {
        foreach (var to in Enum.GetValues<SessionStatus>())
            Assert.False(StateMachines.CanTransition(SessionStatus.Cancelled, to));
        Assert.False(StateMachines.CanTransition(SessionStatus.Published, SessionStatus.Draft));
        Assert.True(StateMachines.CanTransition(SessionStatus.Draft, SessionStatus.Published));
    }

    [Fact]
    public void Organization_requires_approval_before_it_can_be_suspended()
    {
        Assert.True(StateMachines.CanTransition(OrganizationStatus.PendingApproval, OrganizationStatus.Active));
        Assert.False(StateMachines.CanTransition(OrganizationStatus.PendingApproval, OrganizationStatus.Suspended));
        Assert.True(StateMachines.CanTransition(OrganizationStatus.Active, OrganizationStatus.Suspended));
        Assert.True(StateMachines.CanTransition(OrganizationStatus.Suspended, OrganizationStatus.Active));
    }

    [Fact]
    public void Ensure_throws_a_domain_rule_with_a_stable_code()
    {
        var error = Assert.Throws<DomainRuleException>(() => StateMachines.Ensure(false, OrderStatus.Expired, OrderStatus.Paid));
        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
    }
}

public sealed class TimePolicyTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Hold_is_expired_exactly_at_the_expiry_instant()
    {
        var expiresAt = HoldPolicy.ExpiresAt(T, 600);
        Assert.Equal(T.AddMinutes(10), expiresAt);
        Assert.True(HoldPolicy.IsActive(expiresAt.AddTicks(-10), expiresAt));
        Assert.False(HoldPolicy.IsActive(expiresAt, expiresAt));
        Assert.False(HoldPolicy.IsActive(expiresAt.AddTicks(10), expiresAt));
    }

    [Fact]
    public void Expiry_is_truncated_to_microseconds_so_it_round_trips_through_postgres()
    {
        var withSubMicrosecond = T.AddTicks(7); // 700 ns
        var expiresAt = HoldPolicy.ExpiresAt(withSubMicrosecond, 600);
        Assert.Equal(0, expiresAt.Ticks % 10);
        Assert.Equal(T.AddMinutes(10), expiresAt);
        Assert.Equal(TimeSpan.Zero, expiresAt.Offset);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void Ticket_count_limits(int count, bool valid) => Assert.Equal(valid, HoldPolicy.IsValidTicketCount(count));

    [Fact]
    public void Sales_window_is_half_open()
    {
        var start = T;
        var end = T.AddHours(2);
        Assert.Equal(SalesWindowState.NotStarted, SalesWindow.Evaluate(start.AddTicks(-10), start, end));
        Assert.Equal(SalesWindowState.Open, SalesWindow.Evaluate(start, start, end));
        Assert.Equal(SalesWindowState.Open, SalesWindow.Evaluate(end.AddTicks(-10), start, end));
        Assert.Equal(SalesWindowState.Closed, SalesWindow.Evaluate(end, start, end));
    }

    [Fact]
    public void Sales_window_definition_must_end_no_later_than_the_session_start()
    {
        Assert.True(SalesWindow.IsValidDefinition(T, T.AddHours(1), T.AddHours(1)));
        Assert.False(SalesWindow.IsValidDefinition(T, T.AddHours(2), T.AddHours(1)));
        Assert.False(SalesWindow.IsValidDefinition(T, T, T.AddHours(1)));
    }

    [Fact]
    public void Customer_cancellation_closes_exactly_at_the_cutoff()
    {
        var startsAt = T.AddDays(3);
        var deadline = CancellationPolicy.CancelDeadline(startsAt, CancellationPolicy.DefaultCutoff);
        Assert.Equal(startsAt.AddHours(-24), deadline);
        Assert.True(CancellationPolicy.CustomerCanCancel(deadline.AddTicks(-10), startsAt, CancellationPolicy.DefaultCutoff));
        Assert.False(CancellationPolicy.CustomerCanCancel(deadline, startsAt, CancellationPolicy.DefaultCutoff));
    }

    [Fact]
    public void Check_in_window_is_half_open_around_the_session_start()
    {
        var before = CheckInWindow.DefaultOpensBefore;
        var after = CheckInWindow.DefaultClosesAfter;
        Assert.False(CheckInWindow.IsOpen(T.AddHours(-3).AddTicks(-10), T, before, after));
        Assert.True(CheckInWindow.IsOpen(T.AddHours(-3), T, before, after));
        Assert.True(CheckInWindow.IsOpen(T.AddHours(6).AddTicks(-10), T, before, after));
        Assert.False(CheckInWindow.IsOpen(T.AddHours(6), T, before, after));
    }
}

public sealed class LocalTimeResolverTests
{
    [Theory]
    [InlineData("Europe/Istanbul", true)]
    [InlineData("Europe/Berlin", true)]
    [InlineData("UTC", true)]
    [InlineData("Turkey Standard Time", false)] // Windows kimliği; IANA değil
    [InlineData("Mars/Olympus", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_iana_time_zone_ids_are_accepted(string? id, bool expected) =>
        Assert.Equal(expected, LocalTimeResolver.TryFindTimeZone(id, out _));

    [Fact]
    public void Local_time_is_converted_with_the_venue_offset()
    {
        var resolved = LocalTimeResolver.Resolve(new DateTime(2026, 11, 20, 20, 30, 0), "Europe/Istanbul");
        Assert.True(resolved.Success);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 17, 30, 0, TimeSpan.Zero), resolved.Utc);
        Assert.Equal(new DateTime(2026, 11, 20, 20, 30, 0), LocalTimeResolver.ToLocal(resolved.Utc, "Europe/Istanbul"));
    }

    [Fact]
    public void Nonexistent_and_ambiguous_local_times_are_rejected_instead_of_silently_adjusted()
    {
        // Europe/Berlin: 2026-03-29 02:30 yok (ileri alınır); 2026-10-25 02:30 iki kez yaşanır.
        var gap = LocalTimeResolver.Resolve(new DateTime(2026, 3, 29, 2, 30, 0), "Europe/Berlin");
        Assert.False(gap.Success);
        Assert.Equal(ErrorCodes.InvalidLocalTime, gap.ErrorCode);

        var overlap = LocalTimeResolver.Resolve(new DateTime(2026, 10, 25, 2, 30, 0), "Europe/Berlin");
        Assert.False(overlap.Success);
        Assert.Equal(ErrorCodes.InvalidLocalTime, overlap.ErrorCode);
    }

    [Fact]
    public void Unknown_time_zone_is_reported_with_its_own_code()
    {
        var resolved = LocalTimeResolver.Resolve(new DateTime(2026, 11, 20, 20, 30, 0), "Nowhere/Land");
        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.InvalidTimezone, resolved.ErrorCode);
    }
}

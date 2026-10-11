namespace Loca.Domain;

public enum OrganizationStatus { PendingApproval, Active, Suspended }

public enum MemberRole { OrganizerAdmin, GateStaff }

public enum EventStatus { Draft, Published, Cancelled }

public enum EventCategory { Concert, Theatre, Festival, Sports, Standup, Conference, Other }

public enum SessionStatus { Draft, Published, Cancelled }

public enum SalesMode { ReservedSeating, GeneralAdmission }

public enum SeatStatus { Available, Held, Sold }

public enum ReservationStatus { Active, Consumed, Expired, Released }

public enum ReservationLineKind { Seat, GeneralAdmission }

public enum OrderStatus { PendingPayment, Paid, Failed, Expired, Cancelled }

public enum OrderCancelReason { Customer, SessionCancelled }

public enum PaymentStatus { Initiated, Pending, Unknown, Succeeded, Declined, Failed }

/// <summary>Ret (kart/işlem) ile sağlayıcı hatası bilinçli olarak ayrı tutulur.</summary>
public enum PaymentFailureKind { Declined, ProviderError }

public enum RefundStatus { Pending, Succeeded, Failed }

public enum RefundReason { CustomerCancellation, SessionCancellation, LatePaymentCompensation }

public enum TicketStatus { Valid, CheckedIn, Cancelled }

public enum ReconciliationOutcome { Planned, Attempted, Succeeded, Failed }

public enum CancellationJobStatus { Pending, Completed }

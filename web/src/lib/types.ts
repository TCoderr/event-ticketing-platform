// Sunucu DTO'larının istemci karşılıkları. Kaynak: src/Loca.Api/Modules/**/…Endpoints.cs (JSON camelCase, enum metin).

export type OrganizationStatus = 'PendingApproval' | 'Active' | 'Suspended';
export type MemberRole = 'OrganizerAdmin' | 'GateStaff';
export type EventStatus = 'Draft' | 'Published' | 'Cancelled';
export type EventCategory = 'Concert' | 'Theatre' | 'Festival' | 'Sports' | 'Standup' | 'Conference' | 'Other';
export type SessionStatus = 'Draft' | 'Published' | 'Cancelled';
export type SalesMode = 'ReservedSeating' | 'GeneralAdmission';
export type SalesWindowState = 'NotStarted' | 'Open' | 'Closed';
export type HoldStatus = 'Active' | 'Consumed' | 'Expired' | 'Released';
export type OrderStatus = 'PendingPayment' | 'Paid' | 'Failed' | 'Expired' | 'Cancelled';
export type OrderCancelReason = 'Customer' | 'SessionCancelled';
export type PaymentStatus = 'Initiated' | 'Pending' | 'Unknown' | 'Succeeded' | 'Declined' | 'Failed';
export type PaymentFailureKind = 'Declined' | 'ProviderError';
export type RefundStatus = 'Pending' | 'Succeeded' | 'Failed';
export type RefundReason = 'CustomerCancellation' | 'SessionCancellation' | 'LatePaymentCompensation';
export type TicketStatus = 'Valid' | 'CheckedIn' | 'Cancelled';
export type ReconciliationOutcome = 'Planned' | 'Attempted' | 'Succeeded' | 'Failed';
export type CancellationJobStatus = 'Pending' | 'Completed';

export interface Membership {
  organizationId: string;
  organizationName: string;
  organizationStatus: OrganizationStatus;
  role: MemberRole;
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  isPlatformAdmin: boolean;
  isDemo: boolean;
  memberships: Membership[];
}

export interface LoginResponse {
  csrfToken: string;
  user: Me;
}

// --- Public katalog ---------------------------------------------------------------------------

export interface PublicSessionListItem {
  sessionId: string;
  eventSlug: string;
  eventTitle: string;
  category: EventCategory;
  city: string;
  venueName: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  salesMode: SalesMode;
  minPriceMinor: number | null;
  currency: string;
  salesState: SalesWindowState;
  salesStartAt: string;
  soldOut: boolean;
  isDemo: boolean;
}

export interface PublicSessionPage {
  items: PublicSessionListItem[];
  nextCursor: string | null;
  serverTime: string;
}

export interface PublicFilters {
  cities: string[];
  categories: EventCategory[];
}

export interface PublicCategory {
  id: string;
  name: string;
  priceMinor: number;
  available: number;
}

export interface PublicSessionDetail {
  sessionId: string;
  eventSlug: string;
  eventTitle: string;
  eventDescription: string;
  category: EventCategory;
  organizerName: string;
  venueName: string;
  venueAddress: string;
  city: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  salesMode: SalesMode;
  salesStartAt: string;
  salesEndAt: string;
  salesState: SalesWindowState;
  holdTtlSeconds: number;
  maxTicketsPerHold: number;
  currency: string;
  demoSimulatorEnabled: boolean;
  isDemo: boolean;
  seats: { total: number; available: number } | null;
  tiers: { tierCode: string; priceMinor: number }[];
  categories: PublicCategory[];
  serverTime: string;
}

export interface PublicSeat {
  id: string;
  section: string;
  row: string;
  number: number;
  x: number;
  y: number;
  tierCode: string;
  priceMinor: number;
  available: boolean;
}

export interface PublicSeatMap {
  sessionId: string;
  serverTime: string;
  seats: PublicSeat[];
}

export interface PublicEventDetail {
  slug: string;
  title: string;
  description: string;
  category: EventCategory;
  organizerName: string;
  isDemo: boolean;
  sessions: PublicSessionListItem[];
  serverTime: string;
}

// --- Hold / sipariş / bilet --------------------------------------------------------------------

export interface HoldLine {
  lineNo: number;
  kind: 'Seat' | 'GeneralAdmission';
  label: string;
  quantity: number;
  unitPriceMinor: number;
  seatId: string | null;
  categoryId: string | null;
}

export interface Hold {
  id: string;
  sessionId: string;
  status: HoldStatus;
  expiresAt: string;
  serverTime: string;
  ticketCount: number;
  totalMinor: number;
  currency: string;
  lines: HoldLine[];
  orderId: string | null;
  guarantee: string;
}

export interface OrderPayment {
  status: PaymentStatus;
  failureKind: PaymentFailureKind | null;
  failureCode: string | null;
  lateSuccess: boolean;
}

export interface OrderRefund {
  status: RefundStatus;
  reason: RefundReason;
  amountMinor: number;
}

export interface Order {
  id: string;
  orderNumber: string;
  status: OrderStatus;
  totalMinor: number;
  currency: string;
  createdAt: string;
  paidAt: string | null;
  closedAt: string | null;
  cancelReason: OrderCancelReason | null;
  holdId: string;
  holdExpiresAt: string;
  sessionId: string;
  eventTitle: string;
  venueName: string;
  city: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  sessionStatus: SessionStatus;
  ticketCount: number;
  lines: HoldLine[];
  payment: OrderPayment;
  refund: OrderRefund | null;
  canCancel: boolean;
  cancelDeadline: string;
  serverTime: string;
  note: string;
  isDemo: boolean;
}

export interface TicketListItem {
  id: string;
  orderId: string;
  sessionId: string;
  eventTitle: string;
  venueName: string;
  city: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  label: string;
  status: TicketStatus;
  issuedAt: string;
  checkedInAt: string | null;
  isDemo: boolean;
}

export interface TicketDetail extends TicketListItem {
  venueAddress: string;
  checkInOpensAt: string;
  checkInClosesAt: string;
  token: string | null;
  serverTime: string;
}

// --- Kapı --------------------------------------------------------------------------------------

export interface GateSession {
  sessionId: string;
  eventTitle: string;
  venueName: string;
  city: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  status: SessionStatus;
  checkInOpensAt: string;
  checkInClosesAt: string;
  windowOpen: boolean;
  ticketsTotal: number;
  ticketsCheckedIn: number;
  serverTime: string;
}

export interface CheckInResponse {
  result: 'Accepted';
  ticketId: string;
  label: string;
  checkedInAt: string;
  gate: string | null;
}

export interface GateStaff {
  userId: string;
  displayName: string;
  assigned: boolean;
}

// --- Organizatör -------------------------------------------------------------------------------

export interface Organization {
  id: string;
  name: string;
  slug: string;
  status: OrganizationStatus;
  isDemo: boolean;
  myRole: MemberRole | null;
}

export interface Member {
  userId: string;
  displayName: string;
  email: string;
  role: MemberRole;
}

export interface Venue {
  id: string;
  organizationId: string;
  name: string;
  city: string;
  address: string;
  timezone: string;
}

export interface LayoutSection {
  name: string;
  tierCode: string;
  rows: number;
  seatsPerRow: number;
  aisleAfter?: number | null;
}

export interface Layout {
  id: string;
  venueId: string;
  version: number;
  name: string;
  seatCount: number;
  createdAt: string;
  tierCodes: string[];
}

export interface OrganizerSessionSummary {
  id: string;
  startsAt: string;
  startsAtLocal: string;
  timezone: string;
  venueName: string;
  city: string;
  salesMode: SalesMode;
  status: SessionStatus;
}

export interface OrganizerEvent {
  id: string;
  organizationId: string;
  title: string;
  slug: string;
  description: string;
  category: EventCategory;
  status: EventStatus;
  moderationBlocked: boolean;
  isDemo: boolean;
  sessions: OrganizerSessionSummary[];
}

export interface OrganizerCategory {
  id: string;
  name: string;
  priceMinor: number;
  capacity: number;
  held: number;
  sold: number;
  available: number;
}

export interface OrganizerSession {
  id: string;
  eventId: string;
  eventTitle: string;
  venueId: string;
  venueName: string;
  city: string;
  timezone: string;
  startsAt: string;
  startsAtLocal: string;
  salesStartAt: string;
  salesEndAt: string;
  salesMode: SalesMode;
  status: SessionStatus;
  holdTtlSeconds: number;
  layoutId: string | null;
  demoSimulatorEnabled: boolean;
  currency: string;
  seats: { total: number; available: number; held: number; sold: number } | null;
  tiers: { tierCode: string; priceMinor: number; seatCount: number }[];
  categories: OrganizerCategory[];
}

export interface SessionCancellation {
  sessionId: string;
  status: SessionStatus;
  jobStatus: CancellationJobStatus;
  processedCount: number;
  refundCount: number;
}

export interface SessionSummary {
  sessionId: string;
  eventTitle: string;
  status: SessionStatus;
  salesMode: SalesMode;
  currency: string;
  capacity: number;
  sold: number;
  held: number;
  available: number;
  ticketsSold: number;
  ticketsCheckedIn: number;
  grossCapturedMinor: number;
  refundedMinor: number;
  refundPendingMinor: number;
  netMinor: number;
  ordersPaid: number;
  ordersCancelled: number;
  ordersPendingPayment: number;
  serverTime: string;
}

export interface OperationItem {
  kind: 'payment' | 'refund' | 'cancellation';
  id: string;
  orderId: string | null;
  orderNumber: string | null;
  sessionId: string;
  eventTitle: string;
  status: string;
  detail: string | null;
  amountMinor: number;
  lateSuccess: boolean;
  attemptCount: number;
  updatedAt: string;
  actions: ('reconcile' | 'retry')[];
}

export interface OperationRun {
  id: string;
  targetType: 'payment' | 'refund';
  targetId: string;
  outcome: ReconciliationOutcome;
  detailCode: string;
  createdAt: string;
}

export interface OperationCounts {
  unknownPayments: number;
  pendingPayments: number;
  reviewPayments: number;
  lateSuccesses: number;
  pendingRefunds: number;
  failedRefunds: number;
  pendingCancellationJobs: number;
  runsSucceeded: number;
  runsFailed: number;
  runsAttempted: number;
  runsPlanned: number;
}

export interface Operations {
  counts: OperationCounts;
  items: OperationItem[];
  recentRuns: OperationRun[];
  serverTime: string;
}

/** Operatör eyleminin sonucu: planlandı / denendi / sonuç ayrı alanlardır. */
export interface OperatorRun {
  runId: string;
  targetType: 'payment' | 'refund';
  targetId: string;
  planned: boolean;
  attempted: boolean;
  outcome: ReconciliationOutcome;
  detailCode: string;
}

// --- Platform yöneticisi -----------------------------------------------------------------------

export interface AdminOrganization {
  id: string;
  name: string;
  slug: string;
  status: OrganizationStatus;
  isDemo: boolean;
  createdAt: string;
  memberCount: number;
}

export interface AdminEvent {
  id: string;
  title: string;
  slug: string;
  organizationName: string;
  status: EventStatus;
  moderationBlocked: boolean;
  isDemo: boolean;
}

export interface DeadLetter {
  id: string;
  type: string;
  attemptCount: number;
  lastErrorCode: string | null;
  createdAt: string;
  deadLetteredAt: string | null;
}

export interface DemoPspCharge {
  id: string;
  merchantReference: string;
  status: string;
  declineCode: string | null;
  sequence: number;
  amountMinor: number;
  currency: string;
}

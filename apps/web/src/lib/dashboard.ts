/**
 * dashboard.ts
 * Tipos TypeScript para los endpoints del Dashboard MVP.
 * Espejo de los DTOs del backend (.NET).
 */

export interface DashboardSummary {
  totalRecoveredRevenue: number
  recoveredAppointments: number
  activeConversations: number
  messagesSent: number
  messagesDelivered: number
  messagesRead: number
  messagesFailed: number
  deliveryRate: number
  readRate: number
  pendingHumanHandoff: number
}

export interface DailyDeliveryPoint {
  date: string      // "YYYY-MM-DD"
  sent: number
  delivered: number
  read: number
  failed: number
}

export interface DeliveryByFlow {
  flowId: string
  sent: number
  delivered: number
  failed: number
  deliveryRate: number
}

export interface MessageDeliveryData {
  daily: DailyDeliveryPoint[]
  byFlow: DeliveryByFlow[]
}

export interface FlowPerformanceRow {
  flowId: string
  flowLabel: string
  sent: number
  delivered: number
  read: number
  replies: number
  bookings: number
  recoveredRevenue: number
  conversionRate: number
}

export interface FlowPerformance {
  flows: FlowPerformanceRow[]
}

export interface ConversationRow {
  conversationId: string
  patientName: string
  patientPhone: string
  lastMessagePreview: string | null
  lastDirection: string
  lastDeliveryStatus: string
  flowId: string
  status: string
  requiresHuman: boolean
  updatedAt: string   // ISO 8601
}

export interface ConversationsData {
  items: ConversationRow[]
  totalCount: number
  page: number
  pageSize: number
  hasMore: boolean
}

export interface RevenueByEventType {
  eventType: string
  count: number
  totalAmount: number
  successFeeAmount: number
}

export interface RevenueByDay {
  date: string
  amount: number
  count: number
}

export interface RevenueOverview {
  totalRevenue: number
  totalSuccessFee: number
  totalEvents: number
  byEventType: RevenueByEventType[]
  byDay: RevenueByDay[]
}

export interface DashboardFilters {
  dateFrom?: string   // "YYYY-MM-DD"
  dateTo?: string     // "YYYY-MM-DD"
  flowId?: string
}

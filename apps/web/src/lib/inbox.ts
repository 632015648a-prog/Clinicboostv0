/**
 * inbox.ts
 * Tipos TypeScript para la Inbox operacional de ClinicBoost.
 * Espejo de los DTOs del backend (.NET Features/Conversations).
 */

// ── Lista de conversaciones ────────────────────────────────────────────────

export interface InboxConversationItem {
  conversationId:     string
  patientName:        string
  patientPhone:       string
  flowId:             string
  status:             ConversationStatus
  requiresHuman:      boolean
  lastMessagePreview: string | null
  lastDirection:      'inbound' | 'outbound' | ''
  lastDeliveryStatus: string
  messageCount:       number
  lastMessageAt:      string    // ISO 8601
  updatedAt:          string    // ISO 8601
  createdAt:          string    // ISO 8601
}

export interface InboxListResponse {
  items:             InboxConversationItem[]
  totalCount:        number
  page:              number
  pageSize:          number
  hasMore:           boolean
  waitingHumanCount: number     // badge global: conversaciones waiting_human
}

// ── Detalle de conversación ────────────────────────────────────────────────

export interface InboxMessageItem {
  messageId:    string
  direction:    'inbound' | 'outbound'
  body:         string | null
  status:       string
  generatedByAi: boolean
  aiModel:      string | null
  templateId:   string | null
  mediaUrl:     string | null
  mediaType:    string | null
  sentAt:       string | null   // ISO 8601
  deliveredAt:  string | null
  readAt:       string | null
  createdAt:    string          // ISO 8601
}

export interface StatusChangeItem {
  timestamp:      string
  previousStatus: string
  newStatus:      string
  note?:          string | null
  actorId?:       string | null
}

export interface ConversationDetailResponse {
  conversationId: string
  patientName:    string
  patientPhone:   string
  flowId:         string
  status:         ConversationStatus
  requiresHuman:  boolean
  createdAt:      string
  messages:       InboxMessageItem[]
  statusHistory:  StatusChangeItem[]
}

// ── Mutation PATCH status ─────────────────────────────────────────────────

export type ConversationStatus =
  | 'open'
  | 'waiting_ai'
  | 'waiting_human'
  | 'resolved'
  | 'expired'
  | 'opted_out'

export type PatchableStatus = 'waiting_human' | 'open' | 'resolved'

export interface PatchConversationStatusRequest {
  status: PatchableStatus
  note?:  string
}

export interface PatchConversationStatusResponse {
  conversationId: string
  newStatus:      string
  previousStatus: string
  note?:          string | null
  updatedAt:      string
}

// ── Filtros de la Inbox ────────────────────────────────────────────────────

export interface InboxFilters {
  status?:        ConversationStatus | 'all'
  flowId?:        string
  requiresHuman?: boolean
  search?:        string
  page?:          number
  pageSize?:      number
}

// ── Envío manual de mensaje por operador ──────────────────────────────────

/** Body de POST /api/conversations/{id}/messages */
export interface SendManualMessageRequest {
  body: string
}

/** Respuesta de POST /api/conversations/{id}/messages */
export interface SendManualMessageResponse {
  messageId:  string
  direction:  'outbound'
  body:       string
  status:     string       // sent | failed
  twilioSid:  string | null
  createdAt:  string       // ISO 8601
}

// ── Pending handoff (widget de polling del dashboard) ─────────────────────

/** Fila del widget "intervención humana pendiente" en el dashboard. */
export interface PendingHandoffItem {
  conversationId: string
  patientName:    string
  patientPhone:   string
  flowId:         string
  /** Minutos desde la última actualización del estado (tiempo de espera aproximado). */
  waitingMinutes: number
  updatedAt:      string   // ISO 8601
}

/** Respuesta de GET /api/conversations/pending-handoff */
export interface PendingHandoffResponse {
  count: number
  items: PendingHandoffItem[]
}

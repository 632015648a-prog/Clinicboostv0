/**
 * useInbox.ts
 * Hooks React Query para la Inbox operacional.
 * Incluye queries (lista + detalle) y mutations (PATCH status).
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  InboxListResponse,
  ConversationDetailResponse,
  PatchConversationStatusRequest,
  PatchConversationStatusResponse,
  SendManualMessageRequest,
  SendManualMessageResponse,
  InboxFilters,
} from './inbox'

// ── Cache keys ────────────────────────────────────────────────────────────

export const inboxKeys = {
  all:    ['inbox'] as const,
  list:   (filters: InboxFilters) => ['inbox', 'list', filters] as const,
  detail: (id: string)            => ['inbox', 'detail', id] as const,
}

// ── Helpers ───────────────────────────────────────────────────────────────

function buildInboxParams(filters: InboxFilters): Record<string, string> {
  const p: Record<string, string> = {}
  if (filters.status        && filters.status !== 'all') p['Status']        = filters.status
  if (filters.flowId)                                    p['FlowId']        = filters.flowId
  if (filters.search)                                    p['Search']        = filters.search
  if (filters.requiresHuman != null)                     p['RequiresHuman'] = String(filters.requiresHuman)
  p['Page']     = String(filters.page     ?? 1)
  p['PageSize'] = String(filters.pageSize ?? 20)
  return p
}

// ── GET /api/conversations ────────────────────────────────────────────────

export function useInboxList(filters: InboxFilters) {
  return useQuery<InboxListResponse>({
    queryKey: inboxKeys.list(filters),
    queryFn: async () => {
      const { data } = await api.get<InboxListResponse>('/api/conversations', {
        params: buildInboxParams(filters),
      })
      return data
    },
    staleTime:       1000 * 30,
    refetchInterval: 1000 * 30,
  })
}

// ── GET /api/conversations/{id}/messages ─────────────────────────────────

export function useConversationDetail(conversationId: string | null) {
  return useQuery<ConversationDetailResponse>({
    queryKey: inboxKeys.detail(conversationId ?? ''),
    queryFn: async () => {
      const { data } = await api.get<ConversationDetailResponse>(
        `/api/conversations/${conversationId}/messages`
      )
      return data
    },
    enabled:         !!conversationId,
    staleTime:       1000 * 30,
    refetchInterval: 1000 * 30,
  })
}

// ── PATCH /api/conversations/{id}/status ─────────────────────────────────

export function usePatchConversationStatus() {
  const qc = useQueryClient()

  return useMutation<
    PatchConversationStatusResponse,
    Error,
    { conversationId: string; body: PatchConversationStatusRequest }
  >({
    mutationFn: async ({ conversationId, body }) => {
      const { data } = await api.patch<PatchConversationStatusResponse>(
        `/api/conversations/${conversationId}/status`,
        body
      )
      return data
    },
    onSuccess: (_data, { conversationId }) => {
      // Invalidar lista completa y el detalle específico
      qc.invalidateQueries({ queryKey: inboxKeys.all })
      qc.invalidateQueries({ queryKey: inboxKeys.detail(conversationId) })
    },
  })
}

// ── POST /api/conversations/{id}/messages ─────────────────────────────────

/**
 * Envía un mensaje manual del operador en una conversación.
 * Invalida el detalle para que el historial se refresque inmediatamente.
 */
export function useSendManualMessage() {
  const qc = useQueryClient()

  return useMutation<
    SendManualMessageResponse,
    Error,
    { conversationId: string; body: SendManualMessageRequest }
  >({
    mutationFn: async ({ conversationId, body }) => {
      const { data } = await api.post<SendManualMessageResponse>(
        `/api/conversations/${conversationId}/messages`,
        body
      )
      return data
    },
    onSuccess: (_data, { conversationId }) => {
      // Refrescar el historial de mensajes del detalle
      qc.invalidateQueries({ queryKey: inboxKeys.detail(conversationId) })
    },
  })
}

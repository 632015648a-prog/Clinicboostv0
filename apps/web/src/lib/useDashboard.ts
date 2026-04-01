/**
 * useDashboard.ts
 * Hooks React Query para los endpoints del Dashboard MVP.
 */

import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type {
  DashboardSummary,
  MessageDeliveryData,
  FlowPerformance,
  ConversationsData,
  RevenueOverview,
  DashboardFilters,
} from './dashboard'

// ── Helpers ────────────────────────────────────────────────────────────────

function buildParams(filters: DashboardFilters, extra?: Record<string, unknown>) {
  const params: Record<string, string> = {}
  if (filters.dateFrom) params['DateFrom'] = filters.dateFrom
  if (filters.dateTo)   params['DateTo']   = filters.dateTo
  if (filters.flowId)   params['FlowId']   = filters.flowId
  if (extra) {
    Object.entries(extra).forEach(([k, v]) => {
      if (v != null) params[k] = String(v)
    })
  }
  return params
}

const STALE_TIME = 1000 * 60 * 2   // 2 min

// ── GET /api/dashboard/summary ─────────────────────────────────────────────

export function useDashboardSummary(filters: DashboardFilters) {
  return useQuery<DashboardSummary>({
    queryKey: ['dashboard', 'summary', filters],
    queryFn: async () => {
      const { data } = await api.get<DashboardSummary>('/api/dashboard/summary', {
        params: buildParams(filters),
      })
      return data
    },
    staleTime: STALE_TIME,
  })
}

// ── GET /api/dashboard/message-delivery ────────────────────────────────────

export function useMessageDelivery(filters: DashboardFilters) {
  return useQuery<MessageDeliveryData>({
    queryKey: ['dashboard', 'message-delivery', filters],
    queryFn: async () => {
      const { data } = await api.get<MessageDeliveryData>('/api/dashboard/message-delivery', {
        params: buildParams(filters),
      })
      return data
    },
    staleTime: STALE_TIME,
  })
}

// ── GET /api/dashboard/flow-performance ────────────────────────────────────

export function useFlowPerformance(filters: DashboardFilters) {
  return useQuery<FlowPerformance>({
    queryKey: ['dashboard', 'flow-performance', filters],
    queryFn: async () => {
      const { data } = await api.get<FlowPerformance>('/api/dashboard/flow-performance', {
        params: buildParams(filters),
      })
      return data
    },
    staleTime: STALE_TIME,
  })
}

// ── GET /api/dashboard/conversations ───────────────────────────────────────

export function useConversations(
  filters: DashboardFilters,
  extra?: { page?: number; pageSize?: number; status?: string; requiresHuman?: boolean }
) {
  return useQuery<ConversationsData>({
    queryKey: ['dashboard', 'conversations', filters, extra],
    queryFn: async () => {
      const { data } = await api.get<ConversationsData>('/api/dashboard/conversations', {
        params: buildParams(filters, {
          Page:          extra?.page     ?? 1,
          PageSize:      extra?.pageSize ?? 20,
          Status:        extra?.status,
          RequiresHuman: extra?.requiresHuman,
        }),
      })
      return data
    },
    staleTime: STALE_TIME,
  })
}

// ── GET /api/dashboard/revenue-overview ────────────────────────────────────

export function useRevenueOverview(filters: DashboardFilters) {
  return useQuery<RevenueOverview>({
    queryKey: ['dashboard', 'revenue-overview', filters],
    queryFn: async () => {
      const { data } = await api.get<RevenueOverview>('/api/dashboard/revenue-overview', {
        params: buildParams(filters),
      })
      return data
    },
    staleTime: STALE_TIME,
  })
}

/**
 * usePendingHandoff.ts
 *
 * Hook de polling para conversaciones en estado waiting_human.
 * Actualiza automáticamente en segundo plano (configurable, por defecto 30 s).
 *
 * Uso:
 *   const { data, isLoading, isError } = usePendingHandoff()
 *   const { data }                     = usePendingHandoff(60_000)  // 60 s
 */

import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type { PendingHandoffResponse } from './inbox'

export const pendingHandoffKey = ['conversations', 'pending-handoff'] as const

export function usePendingHandoff(pollIntervalMs = 30_000) {
  return useQuery<PendingHandoffResponse>({
    queryKey: pendingHandoffKey,
    queryFn: async () => {
      const { data } = await api.get<PendingHandoffResponse>(
        '/api/conversations/pending-handoff'
      )
      return data
    },
    staleTime:                  pollIntervalMs,
    refetchInterval:            pollIntervalMs,
    refetchIntervalInBackground: true,
  })
}

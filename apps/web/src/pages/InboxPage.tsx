/**
 * InboxPage.tsx
 * Bandeja de entrada operacional de ClinicBoost.
 *
 * Layout (2 columnas en escritorio):
 *   ┌──────────────────────────────┬──────────────────────────────────┐
 *   │  COLUMNA IZQUIERDA           │  COLUMNA DERECHA (detalle)        │
 *   │  ─ Header con badges         │  ─ Cabecera del paciente           │
 *   │  ─ Barra de filtros          │  ─ Acciones (cambiar estado)       │
 *   │  ─ Lista de conversaciones   │  ─ Historial de mensajes           │
 *   │  ─ Paginación                │                                    │
 *   └──────────────────────────────┴──────────────────────────────────┘
 *
 * En móvil: lista primero; al seleccionar una conv se muestra el detalle
 * en pantalla completa con botón de volver.
 */

import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  useInboxList,
  useConversationDetail,
  usePatchConversationStatus,
  useSendManualMessage,
} from '../lib/useInbox'
import type { InboxFilters, ConversationStatus, PatchableStatus, StatusChangeItem } from '../lib/inbox'

// ─── Constantes ───────────────────────────────────────────────────────────────

const FLOW_LABELS: Record<string, string> = {
  flow_00: 'General',
  flow_01: 'Llamada perdida',
  flow_02: 'Huecos',
  flow_03: 'Recordatorio',
  flow_04: 'No-show',
  flow_05: 'Lista espera',
  flow_06: 'Reactivación',
  flow_07: 'Reprogramación',
}

const STATUS_LABELS: Record<string, string> = {
  open:           'Abierta',
  waiting_ai:     'Esperando IA',
  waiting_human:  'Espera humano',
  resolved:       'Resuelta',
  expired:        'Expirada',
  opted_out:      'Opt-out',
}

// ─── Utilidades ───────────────────────────────────────────────────────────────

function relativeTime(iso: string): string {
  const diff = (Date.now() - new Date(iso).getTime()) / 1000
  if (diff < 60)    return 'ahora'
  if (diff < 3600)  return `hace ${Math.floor(diff / 60)}m`
  if (diff < 86400) return `hace ${Math.floor(diff / 3600)}h`
  return `hace ${Math.floor(diff / 86400)}d`
}

function fmtTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleTimeString('es-ES', {
    hour:   '2-digit',
    minute: '2-digit',
  })
}

function fmtDate(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleDateString('es-ES', {
    day:   '2-digit',
    month: 'short',
    hour:  '2-digit',
    minute: '2-digit',
  })
}

// ─── Colores por estado ────────────────────────────────────────────────────────

function statusCls(status: string): string {
  const map: Record<string, string> = {
    open:           'bg-blue-100 text-blue-700',
    waiting_ai:     'bg-indigo-100 text-indigo-700',
    waiting_human:  'bg-amber-100 text-amber-800 font-semibold',
    resolved:       'bg-green-100 text-green-700',
    expired:        'bg-gray-100 text-gray-500',
    opted_out:      'bg-red-100 text-red-600',
  }
  return map[status] ?? 'bg-gray-100 text-gray-600'
}

function deliveryCls(s: string): string {
  const map: Record<string, string> = {
    delivered: 'bg-green-100 text-green-700',
    read:      'bg-blue-100 text-blue-700',
    sent:      'bg-gray-100 text-gray-600',
    failed:    'bg-red-100 text-red-600',
    received:  'bg-purple-100 text-purple-700',
  }
  return map[s] ?? 'bg-gray-100 text-gray-500'
}

// ─── Spinner ──────────────────────────────────────────────────────────────────

function Spinner() {
  return (
    <div className="flex items-center justify-center py-10">
      <div className="h-7 w-7 animate-spin rounded-full border-4 border-indigo-500 border-t-transparent" />
    </div>
  )
}

// ─── Panel de envío de mensaje manual ────────────────────────────────────────

const MAX_BODY_LENGTH = 1600

interface SendMessagePanelProps {
  conversationId: string
  currentStatus:  string
}

function SendMessagePanel({ conversationId, currentStatus }: SendMessagePanelProps) {
  const send        = useSendManualMessage()
  const [text, setText]   = useState('')
  const [err,  setErr]    = useState<string | null>(null)

  // Solo visible en estados que permiten envío
  const canSend = currentStatus === 'open' || currentStatus === 'waiting_human'
  if (!canSend) return null

  const charsLeft  = MAX_BODY_LENGTH - text.length
  const isOverflow = charsLeft < 0
  const isEmpty    = text.trim().length === 0

  async function handleSend() {
    setErr(null)

    if (isEmpty) {
      setErr('El mensaje no puede estar vacío.')
      return
    }
    if (isOverflow) {
      setErr(`El mensaje supera el límite de ${MAX_BODY_LENGTH} caracteres.`)
      return
    }

    try {
      await send.mutateAsync({ conversationId, body: { body: text.trim() } })
      setText('')   // limpiar tras envío exitoso
    } catch (e: unknown) {
      // Intentar extraer el mensaje de error del servidor
      const axiosErr = e as { response?: { data?: { error?: string; detail?: string } } }
      const serverMsg =
        axiosErr?.response?.data?.error ??
        axiosErr?.response?.data?.detail ??
        'No se pudo enviar el mensaje. Inténtalo de nuevo.'
      setErr(serverMsg)
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    // Ctrl+Enter o Cmd+Enter para enviar
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
      e.preventDefault()
      handleSend()
    }
  }

  const isLoading = send.isPending

  return (
    <div className="border-t border-gray-200 bg-white px-4 py-3 space-y-2">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        Responder como operador
      </p>

      {err && (
        <div className="rounded-lg bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-700">
          {err}
        </div>
      )}

      <div className="relative">
        <textarea
          placeholder="Escribe un mensaje... (Ctrl+Enter para enviar)"
          value={text}
          onChange={e => { setText(e.target.value); setErr(null) }}
          onKeyDown={handleKeyDown}
          rows={3}
          disabled={isLoading}
          className={`w-full rounded-lg border px-3 py-2 text-sm text-gray-800
                      focus:outline-none focus:ring-2 focus:ring-indigo-400 resize-none
                      disabled:bg-gray-50 disabled:cursor-not-allowed
                      ${isOverflow ? 'border-red-400' : 'border-gray-200'}`}
        />
        <span className={`absolute bottom-2 right-3 text-[10px]
          ${isOverflow ? 'text-red-500 font-semibold' : 'text-gray-400'}`}>
          {charsLeft}
        </span>
      </div>

      <div className="flex items-center justify-between gap-2">
        <p className="text-[10px] text-gray-400">
          {currentStatus === 'waiting_human'
            ? '🙋 La IA permanecerá pausada tras el envío.'
            : '✉️ El mensaje se enviará por WhatsApp.'}
        </p>
        <button
          onClick={handleSend}
          disabled={isLoading || isEmpty || isOverflow}
          className="rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white
                     text-xs font-semibold px-4 py-2 transition-colors
                     disabled:opacity-40 disabled:cursor-not-allowed flex items-center gap-2"
        >
          {isLoading && (
            <span className="h-3 w-3 animate-spin rounded-full border-2
                             border-white border-t-transparent" />
          )}
          Enviar
        </button>
      </div>
    </div>
  )
}

// ─── Panel de acciones ─────────────────────────────────────────────────────────

interface ActionPanelProps {
  conversationId: string
  currentStatus:  string
  onDone:         (newStatus: PatchableStatus) => void
}

function ActionPanel({ conversationId, currentStatus, onDone }: ActionPanelProps) {
  const patch   = usePatchConversationStatus()
  const [note, setNote]   = useState('')
  const [err,  setErr]    = useState<string | null>(null)

  async function apply(newStatus: PatchableStatus) {
    setErr(null)
    try {
      await patch.mutateAsync({ conversationId, body: { status: newStatus, note: note || undefined } })
      onDone(newStatus)
    } catch {
      setErr('No se pudo actualizar el estado. Inténtalo de nuevo.')
    }
  }

  const isLoading = patch.isPending

  return (
    <div className="border-t border-gray-100 bg-gray-50 px-4 py-3 space-y-3">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        Acción operacional
      </p>

      {err && (
        <div className="rounded-lg bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-700">
          {err}
        </div>
      )}

      {/* Campo nota opcional */}
      <textarea
        placeholder="Nota interna (opcional)"
        value={note}
        onChange={e => setNote(e.target.value)}
        rows={2}
        className="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700
                   focus:outline-none focus:ring-2 focus:ring-indigo-400 resize-none"
      />

      <div className="flex flex-wrap gap-2">
        {/* Escalar a humano */}
        {currentStatus !== 'waiting_human' && (
          <button
            disabled={isLoading}
            onClick={() => apply('waiting_human')}
            className="flex-1 min-w-[120px] rounded-lg bg-amber-500 hover:bg-amber-600
                       text-white text-xs font-semibold px-3 py-2 transition-colors
                       disabled:opacity-50 disabled:cursor-not-allowed"
          >
            🙋 Tomar el caso
          </button>
        )}

        {/* Reactivar IA */}
        {currentStatus !== 'open' && currentStatus !== 'resolved' && (
          <button
            disabled={isLoading}
            onClick={() => apply('open')}
            className="flex-1 min-w-[120px] rounded-lg bg-indigo-500 hover:bg-indigo-600
                       text-white text-xs font-semibold px-3 py-2 transition-colors
                       disabled:opacity-50 disabled:cursor-not-allowed"
          >
            🤖 Reactivar IA
          </button>
        )}

        {/* Resolver */}
        {currentStatus !== 'resolved' && (
          <button
            disabled={isLoading}
            onClick={() => apply('resolved')}
            className="flex-1 min-w-[120px] rounded-lg bg-green-500 hover:bg-green-600
                       text-white text-xs font-semibold px-3 py-2 transition-colors
                       disabled:opacity-50 disabled:cursor-not-allowed"
          >
            ✅ Marcar resuelta
          </button>
        )}

        {/* Reabrir si está resuelta */}
        {currentStatus === 'resolved' && (
          <button
            disabled={isLoading}
            onClick={() => apply('open')}
            className="flex-1 min-w-[120px] rounded-lg bg-blue-500 hover:bg-blue-600
                       text-white text-xs font-semibold px-3 py-2 transition-colors
                       disabled:opacity-50 disabled:cursor-not-allowed"
          >
            🔄 Reabrir
          </button>
        )}
      </div>

      {isLoading && (
        <p className="text-xs text-indigo-600 text-center animate-pulse">Guardando...</p>
      )}
    </div>
  )
}

// ─── Historial de cambios de estado ───────────────────────────────────────────

function StatusHistoryPanel({ items }: { items: StatusChangeItem[] }) {
  const [expanded, setExpanded] = useState(false)

  if (items.length === 0) return null

  const visible = expanded ? items : items.slice(0, 3)
  const hasMore = items.length > 3

  return (
    <div className="border-t border-gray-100 bg-white px-4 py-2">
      <p className="text-[10px] font-medium text-gray-400 uppercase tracking-wide mb-1">
        Historial de estado
      </p>
      <div className="space-y-1">
        {visible.map((item, i) => (
          <div key={i} className="flex items-start gap-2 text-[11px] text-gray-500">
            <span className="text-gray-400 shrink-0">{fmtTime(item.timestamp)}</span>
            <span>
              <span className={`px-1 rounded ${statusCls(item.previousStatus)}`}>
                {STATUS_LABELS[item.previousStatus] ?? item.previousStatus}
              </span>
              {' → '}
              <span className={`px-1 rounded ${statusCls(item.newStatus)}`}>
                {STATUS_LABELS[item.newStatus] ?? item.newStatus}
              </span>
              {item.note && (
                <span className="ml-1 italic text-gray-600">— "{item.note}"</span>
              )}
            </span>
          </div>
        ))}
      </div>
      {hasMore && !expanded && (
        <button
          onClick={() => setExpanded(true)}
          className="text-[10px] text-indigo-500 hover:underline mt-1"
        >
          Ver historial completo ({items.length} cambios)
        </button>
      )}
      {hasMore && expanded && (
        <button
          onClick={() => setExpanded(false)}
          className="text-[10px] text-indigo-500 hover:underline mt-1"
        >
          Ocultar
        </button>
      )}
    </div>
  )
}

// ─── Panel de detalle de conversación ─────────────────────────────────────────

interface DetailPanelProps {
  conversationId: string
  onBack:         () => void    // para móvil
}

function DetailPanel({ conversationId, onBack }: DetailPanelProps) {
  const detail = useConversationDetail(conversationId)
  const [statusOverride, setStatusOverride] = useState<string | null>(null)

  const currentStatus = statusOverride ?? detail.data?.status ?? ''

  function handleStatusDone(newStatus: PatchableStatus) {
    // Actualización optimista: muestra el nuevo estado inmediatamente
    // mientras React Query refresca el cache en background.
    setStatusOverride(newStatus)
  }

  if (detail.isLoading) return <div className="flex-1"><Spinner /></div>
  if (detail.isError)   return (
    <div className="flex-1 p-6 text-sm text-red-600">
      Error cargando la conversación. <button onClick={onBack} className="underline">Volver</button>
    </div>
  )
  if (!detail.data) return null

  const d = detail.data

  return (
    <div className="flex flex-col h-full">
      {/* Cabecera del paciente */}
      <div className="px-4 py-3 border-b border-gray-100 bg-white">
        {/* Botón volver en móvil */}
        <button
          onClick={onBack}
          className="lg:hidden mb-2 flex items-center gap-1 text-xs text-indigo-600 hover:underline"
        >
          ← Volver a la lista
        </button>

        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="font-semibold text-gray-900 text-sm">{d.patientName}</p>
            <p className="text-xs text-gray-500 mt-0.5">{d.patientPhone}</p>
            <p className="text-xs text-gray-400 mt-0.5">
              {FLOW_LABELS[d.flowId] ?? d.flowId} · desde {fmtDate(d.createdAt)}
            </p>
          </div>
          <span className={`text-xs px-2 py-1 rounded-full ${statusCls(currentStatus)}`}>
            {STATUS_LABELS[currentStatus] ?? currentStatus}
          </span>
        </div>
      </div>

      {/* Historial de mensajes */}
      <div className="flex-1 overflow-y-auto px-4 py-3 space-y-2 bg-gray-50">
        {d.messages.length === 0 && (
          <p className="text-center text-xs text-gray-400 py-8">Sin mensajes registrados.</p>
        )}
        {d.messages.map(msg => {
          const isInbound = msg.direction === 'inbound'
          return (
            <div
              key={msg.messageId}
              className={`flex ${isInbound ? 'justify-start' : 'justify-end'}`}
            >
              <div
                className={`max-w-[80%] rounded-2xl px-3 py-2 text-sm shadow-sm
                  ${isInbound
                    ? 'bg-white text-gray-800 rounded-bl-sm border border-gray-100'
                    : 'bg-indigo-500 text-white rounded-br-sm'
                  }`}
              >
                {/* Cuerpo */}
                {msg.body ? (
                  <p className="whitespace-pre-wrap break-words leading-relaxed">{msg.body}</p>
                ) : msg.mediaUrl ? (
                  <a
                    href={msg.mediaUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="underline text-xs opacity-80"
                  >
                    📎 Adjunto ({msg.mediaType ?? 'archivo'})
                  </a>
                ) : (
                  <p className="italic opacity-60 text-xs">[ mensaje vacío ]</p>
                )}

                {/* Metadatos */}
                <div className={`flex items-center gap-1.5 mt-1 text-[10px]
                  ${isInbound ? 'text-gray-400' : 'text-indigo-200'}`}>
                  <span>{fmtTime(msg.createdAt)}</span>
                  {!isInbound && (
                    <>
                      <span>·</span>
                      <span className={`px-1 rounded ${deliveryCls(msg.status)}`}>
                        {msg.status}
                      </span>
                    </>
                  )}
                  {msg.generatedByAi && (
                    <>
                      <span>·</span>
                      <span>🤖</span>
                    </>
                  )}
                  {msg.templateId && (
                    <>
                      <span>·</span>
                      <span className="opacity-70">📋 plantilla</span>
                    </>
                  )}
                </div>
              </div>
            </div>
          )
        })}
      </div>

      {/* Historial de cambios de estado */}
      <StatusHistoryPanel items={d.statusHistory ?? []} />

      {/* Panel de envío manual */}
      <SendMessagePanel
        conversationId={conversationId}
        currentStatus={currentStatus}
      />

      {/* Panel de acciones */}
      <ActionPanel
        conversationId={conversationId}
        currentStatus={currentStatus}
        onDone={handleStatusDone}
      />
    </div>
  )
}

// ─── Fila de conversación ─────────────────────────────────────────────────────

interface ConvRowProps {
  item:       ReturnType<typeof useInboxList>['data'] extends infer R
              ? R extends { items: (infer I)[] } ? I : never : never
  isSelected: boolean
  onClick:    () => void
}

function ConvRow({ item, isSelected, onClick }: ConvRowProps) {
  const isUrgent = item.status === 'waiting_human'

  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-4 py-3 transition-colors border-b border-gray-50
        ${isSelected
          ? 'bg-indigo-50 border-l-4 border-l-indigo-500'
          : isUrgent
            ? 'bg-amber-50/60 hover:bg-amber-50 border-l-4 border-l-amber-400'
            : 'hover:bg-gray-50/70 border-l-4 border-l-transparent'
        }`}
    >
      <div className="flex items-start justify-between gap-2">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-1.5 flex-wrap">
            <span className="font-medium text-sm text-gray-900 truncate">
              {item.patientName}
            </span>
            <span className="text-xs text-gray-400 truncate">{item.patientPhone}</span>
            {isUrgent && (
              <span className="text-xs bg-amber-100 text-amber-800 px-1.5 py-0.5 rounded-full font-semibold">
                🙋 Handoff
              </span>
            )}
          </div>

          {item.lastMessagePreview && (
            <p className="mt-0.5 text-xs text-gray-500 truncate">
              {item.lastDirection === 'inbound' ? '← ' : '→ '}
              {item.lastMessagePreview}
            </p>
          )}
        </div>

        <div className="flex flex-col items-end gap-1 shrink-0">
          <span className={`text-[10px] px-1.5 py-0.5 rounded-full ${statusCls(item.status)}`}>
            {STATUS_LABELS[item.status] ?? item.status}
          </span>
          <span className="text-[10px] text-gray-400">
            {relativeTime(item.lastMessageAt)}
          </span>
          <span className={`text-[10px] px-1 rounded ${deliveryCls(item.lastDeliveryStatus)}`}>
            {item.lastDeliveryStatus || '—'}
          </span>
        </div>
      </div>
    </button>
  )
}

// ─── Página principal ─────────────────────────────────────────────────────────

export default function InboxPage() {
  const [searchParams] = useSearchParams()

  const [filters, setFilters] = useState<InboxFilters>(() => {
    const urlStatus  = searchParams.get('status') as ConversationStatus | 'all' | null
    const validStatuses: (ConversationStatus | 'all')[] = [
      'all', 'open', 'waiting_ai', 'waiting_human', 'resolved', 'expired', 'opted_out',
    ]
    return {
      status:   validStatuses.includes(urlStatus as ConversationStatus | 'all')
                  ? (urlStatus as ConversationStatus | 'all')
                  : 'all',
      page:     1,
      pageSize: 25,
    }
  })
  const [selectedId,   setSelectedId]   = useState<string | null>(null)
  const [showDetail,   setShowDetail]   = useState(false)   // para móvil
  const [searchInput,  setSearchInput]  = useState('')

  const list = useInboxList(filters)

  // Número de waiting_human independiente del filtro activo
  const waitingCount = list.data?.waitingHumanCount ?? 0

  function selectConversation(id: string) {
    setSelectedId(id)
    setShowDetail(true)
  }

  function handleBack() {
    setShowDetail(false)
  }

  function applySearch(e: React.FormEvent) {
    e.preventDefault()
    setFilters(f => ({ ...f, search: searchInput.trim() || undefined, page: 1 }))
  }

  function setStatus(s: ConversationStatus | 'all') {
    setFilters(f => ({ ...f, status: s, page: 1 }))
    setSelectedId(null)
    setShowDetail(false)
  }

  const statusTabs: Array<{ key: ConversationStatus | 'all'; label: string; count?: number }> = [
    { key: 'all',           label: 'Todas' },
    { key: 'waiting_human', label: '🙋 Handoff', count: waitingCount },
    { key: 'open',          label: 'Abiertas' },
    { key: 'resolved',      label: 'Resueltas' },
  ]

  return (
    <div className="min-h-screen bg-gray-50 flex flex-col">

      {/* ── Header ──────────────────────────────────────────────────────── */}
      <header className="bg-white border-b border-gray-200 px-6 py-3 flex-shrink-0">
        <div className="max-w-7xl mx-auto flex items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            {/* Volver al dashboard */}
            <Link
              to="/dashboard"
              className="text-xs text-indigo-600 hover:underline flex items-center gap-1"
            >
              ← Dashboard
            </Link>
            <div className="w-px h-4 bg-gray-200" />
            <div className="flex items-center gap-2">
              <span className="text-lg font-semibold text-gray-900">Bandeja de entrada</span>
              {waitingCount > 0 && (
                <span className="bg-amber-500 text-white text-xs font-bold px-2 py-0.5 rounded-full">
                  {waitingCount}
                </span>
              )}
            </div>
          </div>

          {/* Búsqueda rápida */}
          <form onSubmit={applySearch} className="flex items-center gap-2">
            <input
              type="text"
              placeholder="Buscar paciente..."
              value={searchInput}
              onChange={e => setSearchInput(e.target.value)}
              className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm w-44
                         focus:outline-none focus:ring-2 focus:ring-indigo-400"
            />
            <button
              type="submit"
              className="rounded-lg bg-indigo-600 text-white px-3 py-1.5 text-xs font-medium
                         hover:bg-indigo-700 transition-colors"
            >
              Buscar
            </button>
            {filters.search && (
              <button
                type="button"
                onClick={() => { setSearchInput(''); setFilters(f => ({ ...f, search: undefined, page: 1 })) }}
                className="text-xs text-gray-400 hover:text-gray-600"
              >
                ✕
              </button>
            )}
          </form>
        </div>
      </header>

      {/* ── Tabs de estado ───────────────────────────────────────────────── */}
      <div className="bg-white border-b border-gray-100 flex-shrink-0">
        <div className="max-w-7xl mx-auto px-6">
          <div className="flex gap-1 overflow-x-auto">
            {statusTabs.map(tab => (
              <button
                key={tab.key}
                onClick={() => setStatus(tab.key)}
                className={`relative flex items-center gap-1.5 px-4 py-3 text-sm font-medium
                            whitespace-nowrap border-b-2 transition-colors
                            ${filters.status === tab.key
                              ? 'border-indigo-500 text-indigo-600'
                              : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-200'
                            }`}
              >
                {tab.label}
                {tab.count != null && tab.count > 0 && (
                  <span className="bg-amber-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">
                    {tab.count}
                  </span>
                )}
              </button>
            ))}

            {/* Filtro flujo */}
            <div className="ml-auto flex items-center py-2">
              <select
                value={filters.flowId ?? ''}
                onChange={e => setFilters(f => ({
                  ...f,
                  flowId: e.target.value || undefined,
                  page: 1,
                }))}
                className="rounded-lg border border-gray-200 px-2 py-1 text-xs bg-white
                           focus:outline-none focus:ring-2 focus:ring-indigo-400"
              >
                <option value="">Todos los flujos</option>
                {Object.entries(FLOW_LABELS).map(([k, v]) => (
                  <option key={k} value={k}>{k} · {v}</option>
                ))}
              </select>
            </div>
          </div>
        </div>
      </div>

      {/* ── Layout 2 columnas ────────────────────────────────────────────── */}
      <div className="flex-1 max-w-7xl mx-auto w-full flex overflow-hidden" style={{ height: 'calc(100vh - 120px)' }}>

        {/* ── Columna izquierda: lista ───────────────────────────────────── */}
        <div className={`flex flex-col border-r border-gray-200 bg-white overflow-hidden
          ${showDetail ? 'hidden lg:flex' : 'flex'}
          w-full lg:w-96 xl:w-[420px] flex-shrink-0`}
        >
          {/* Estado de la carga */}
          {list.isLoading ? (
            <Spinner />
          ) : list.isError ? (
            <div className="p-6 text-sm text-red-600">
              Error cargando conversaciones. Comprueba que la API está activa.
            </div>
          ) : (list.data?.items.length ?? 0) === 0 ? (
            <div className="flex flex-col items-center justify-center py-16 text-gray-400 text-sm gap-2">
              <span className="text-3xl">📭</span>
              <p>No hay conversaciones en este filtro</p>
              {filters.search && (
                <button
                  onClick={() => { setSearchInput(''); setFilters(f => ({ ...f, search: undefined })) }}
                  className="text-indigo-500 text-xs hover:underline"
                >
                  Limpiar búsqueda
                </button>
              )}
            </div>
          ) : (
            <>
              {/* Contador */}
              <div className="px-4 py-2 bg-gray-50 border-b border-gray-100 text-xs text-gray-500 flex justify-between">
                <span>{list.data!.totalCount} conversaciones</span>
                {list.data!.waitingHumanCount > 0 && (
                  <span className="text-amber-700 font-semibold">
                    {list.data!.waitingHumanCount} esperan atención
                  </span>
                )}
              </div>

              {/* Lista */}
              <div className="flex-1 overflow-y-auto">
                {list.data!.items.map(item => (
                  <ConvRow
                    key={item.conversationId}
                    item={item}
                    isSelected={item.conversationId === selectedId}
                    onClick={() => selectConversation(item.conversationId)}
                  />
                ))}
              </div>

              {/* Paginación */}
              {(list.data!.totalCount > (filters.pageSize ?? 25)) && (
                <div className="px-4 py-2 border-t border-gray-100 flex justify-between items-center text-xs text-gray-500">
                  <span>Pág. {filters.page ?? 1}</span>
                  <div className="flex gap-2">
                    <button
                      disabled={(filters.page ?? 1) <= 1}
                      onClick={() => setFilters(f => ({ ...f, page: Math.max(1, (f.page ?? 1) - 1) }))}
                      className="px-2 py-1 rounded border border-gray-200 hover:bg-gray-50
                                 disabled:opacity-40 disabled:cursor-not-allowed"
                    >
                      ←
                    </button>
                    <button
                      disabled={!list.data?.hasMore}
                      onClick={() => setFilters(f => ({ ...f, page: (f.page ?? 1) + 1 }))}
                      className="px-2 py-1 rounded border border-gray-200 hover:bg-gray-50
                                 disabled:opacity-40 disabled:cursor-not-allowed"
                    >
                      →
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* ── Columna derecha: detalle ───────────────────────────────────── */}
        <div className={`flex-1 overflow-hidden flex flex-col
          ${showDetail ? 'flex' : 'hidden lg:flex'}`}
        >
          {selectedId ? (
            <DetailPanel
              conversationId={selectedId}
              onBack={handleBack}
            />
          ) : (
            <div className="flex-1 flex flex-col items-center justify-center text-gray-300 gap-3">
              <span className="text-5xl">💬</span>
              <p className="text-sm">Selecciona una conversación para ver el detalle</p>
            </div>
          )}
        </div>

      </div>
    </div>
  )
}

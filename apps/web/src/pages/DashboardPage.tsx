/**
 * DashboardPage.tsx
 * Dashboard MVP de ClinicBoost.
 *
 * Estructura:
 *   1. Barra de filtros (rango de fecha + flujo)
 *   2. KPI Cards (summary)
 *   3. Alertas (human handoff + mensajes fallidos)
 *   4. Gráfico de entregabilidad diaria (SVG inline)
 *   5. Tabla de rendimiento por flujo
 *   6. Lista de conversaciones recientes
 *   7. Revenue overview
 */

import { useState, useMemo } from 'react'
import { Link } from 'react-router-dom'
import {
  useDashboardSummary,
  useMessageDelivery,
  useFlowPerformance,
  useConversations,
  useRevenueOverview,
} from '../lib/useDashboard'
import type { DashboardFilters } from '../lib/dashboard'

// ─── Utilidades ─────────────────────────────────────────────────────────────

function fmt(n: number | undefined | null, decimals = 0): string {
  if (n == null) return '—'
  return n.toLocaleString('es-ES', {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  })
}

function fmtEur(n: number | undefined | null): string {
  if (n == null) return '—'
  return n.toLocaleString('es-ES', { style: 'currency', currency: 'EUR' })
}

function todayStr(): string {
  return new Date().toISOString().slice(0, 10)
}

function daysAgoStr(n: number): string {
  const d = new Date()
  d.setDate(d.getDate() - n)
  return d.toISOString().slice(0, 10)
}

// ─── Componentes pequeños ────────────────────────────────────────────────────

function Spinner() {
  return (
    <div className="flex items-center justify-center py-8">
      <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-500 border-t-transparent" />
    </div>
  )
}

function ErrorBanner({ message }: { message: string }) {
  return (
    <div className="rounded-lg bg-red-50 border border-red-200 p-4 text-sm text-red-700">
      <span className="font-medium">Error:</span> {message}
    </div>
  )
}

interface KpiCardProps {
  label: string
  value: string
  sub?: string
  accent?: 'blue' | 'green' | 'yellow' | 'red' | 'purple'
  icon: string
}

function KpiCard({ label, value, sub, accent = 'blue', icon }: KpiCardProps) {
  const colors: Record<string, string> = {
    blue:   'bg-blue-50 text-blue-600',
    green:  'bg-green-50 text-green-600',
    yellow: 'bg-amber-50 text-amber-600',
    red:    'bg-red-50 text-red-600',
    purple: 'bg-purple-50 text-purple-600',
  }
  return (
    <div className="rounded-xl bg-white border border-gray-100 shadow-sm p-5 flex gap-4 items-start">
      <div className={`rounded-lg p-3 text-xl ${colors[accent]}`}>{icon}</div>
      <div>
        <p className="text-xs font-medium text-gray-500 uppercase tracking-wide">{label}</p>
        <p className="mt-1 text-2xl font-bold text-gray-900">{value}</p>
        {sub && <p className="mt-0.5 text-xs text-gray-500">{sub}</p>}
      </div>
    </div>
  )
}

// ─── Gráfico de barras SVG ────────────────────────────────────────────────────

interface BarChartProps {
  data: { label: string; sent: number; delivered: number; failed: number }[]
}

function DeliveryBarChart({ data }: BarChartProps) {
  if (data.length === 0) {
    return (
      <div className="flex items-center justify-center h-40 text-gray-400 text-sm">
        Sin datos en el periodo seleccionado
      </div>
    )
  }

  const maxVal = Math.max(...data.map(d => d.sent), 1)
  const H = 140
  const barW = Math.max(8, Math.min(40, Math.floor(600 / data.length) - 6))
  const gap = 6
  const totalW = data.length * (barW + gap) + gap

  return (
    <div className="overflow-x-auto">
      <svg
        viewBox={`0 0 ${Math.max(totalW, 300)} ${H + 30}`}
        className="w-full min-w-[300px]"
        style={{ height: H + 50 }}
      >
        {data.map((d, i) => {
          const x = gap + i * (barW + gap)
          const sentH    = (d.sent    / maxVal) * H
          const delivH   = (d.delivered / maxVal) * H
          const failH    = (d.failed  / maxVal) * H

          return (
            <g key={d.label}>
              {/* Sent (fondo) */}
              <rect
                x={x} y={H - sentH} width={barW} height={sentH}
                fill="#c7d2fe" rx={3}
              />
              {/* Delivered */}
              <rect
                x={x} y={H - delivH} width={barW * 0.65} height={delivH}
                fill="#6366f1" rx={3}
              />
              {/* Failed */}
              {d.failed > 0 && (
                <rect
                  x={x + barW * 0.65 + 2} y={H - failH} width={barW * 0.3} height={failH}
                  fill="#f87171" rx={3}
                />
              )}
              {/* Label */}
              <text
                x={x + barW / 2} y={H + 15}
                textAnchor="middle"
                fontSize={9}
                fill="#6b7280"
              >
                {d.label.slice(5)} {/* "MM-DD" */}
              </text>
            </g>
          )
        })}
        {/* Eje X */}
        <line x1={0} y1={H} x2={totalW} y2={H} stroke="#e5e7eb" strokeWidth={1} />
      </svg>

      {/* Leyenda */}
      <div className="flex gap-4 mt-1 text-xs text-gray-500">
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded bg-[#c7d2fe]" /> Enviados
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded bg-[#6366f1]" /> Entregados
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded bg-[#f87171]" /> Fallidos
        </span>
      </div>
    </div>
  )
}

// ─── Helpers de estado ────────────────────────────────────────────────────────

function statusBadge(status: string) {
  const map: Record<string, string> = {
    open:           'bg-blue-100 text-blue-700',
    waiting_ai:     'bg-indigo-100 text-indigo-700',
    waiting_human:  'bg-amber-100 text-amber-700',
    resolved:       'bg-green-100 text-green-700',
    expired:        'bg-gray-100 text-gray-600',
    opted_out:      'bg-red-100 text-red-700',
  }
  return map[status] ?? 'bg-gray-100 text-gray-600'
}

function deliveryBadge(s: string) {
  const map: Record<string, string> = {
    delivered: 'bg-green-100 text-green-700',
    read:      'bg-blue-100 text-blue-700',
    sent:      'bg-gray-100 text-gray-700',
    failed:    'bg-red-100 text-red-700',
    received:  'bg-purple-100 text-purple-700',
  }
  return map[s] ?? 'bg-gray-100 text-gray-600'
}

function relativeTime(iso: string): string {
  const diff = (Date.now() - new Date(iso).getTime()) / 1000
  if (diff < 60)    return 'ahora'
  if (diff < 3600)  return `hace ${Math.floor(diff / 60)}m`
  if (diff < 86400) return `hace ${Math.floor(diff / 3600)}h`
  return `hace ${Math.floor(diff / 86400)}d`
}

// ─── Componente principal ─────────────────────────────────────────────────────

export default function DashboardPage() {
  const [filters, setFilters] = useState<DashboardFilters>({
    dateFrom: daysAgoStr(30),
    dateTo:   todayStr(),
  })

  const [convPage, setConvPage] = useState(1)
  const [humanOnly, setHumanOnly] = useState(false)

  // Queries
  const summary   = useDashboardSummary(filters)
  const delivery  = useMessageDelivery(filters)
  const flowPerf  = useFlowPerformance(filters)
  const convs     = useConversations(filters, {
    page:         convPage,
    pageSize:     15,
    requiresHuman: humanOnly || undefined,
  })
  const revenue   = useRevenueOverview(filters)

  // Datos del gráfico diario
  const chartData = useMemo(() => {
    return (delivery.data?.daily ?? []).map(d => ({
      label:     d.date,
      sent:      d.sent,
      delivered: d.delivered,
      failed:    d.failed,
    }))
  }, [delivery.data])

  // Alertas activas
  const hasHumanAlert   = (summary.data?.pendingHumanHandoff ?? 0) > 0
  const hasFailedAlert  = (summary.data?.messagesFailed ?? 0) > 0

  function applyFilters(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const fd = new FormData(e.currentTarget)
    setFilters({
      dateFrom: fd.get('dateFrom') as string || undefined,
      dateTo:   fd.get('dateTo')   as string || undefined,
      flowId:   fd.get('flowId')   as string || undefined,
    })
    setConvPage(1)
  }

  return (
    <div className="min-h-screen bg-gray-50">
      {/* ── Header ────────────────────────────────────────────────────────── */}
      <header className="bg-white border-b border-gray-200 px-6 py-4">
        <div className="max-w-7xl mx-auto flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-8 h-8 rounded-lg bg-indigo-600 flex items-center justify-center text-white font-bold text-sm">
              CB
            </div>
            <div>
              <h1 className="text-lg font-semibold text-gray-900">ClinicBoost</h1>
              <p className="text-xs text-gray-500">Dashboard operacional</p>
            </div>
          </div>
          <div className="flex items-center gap-3">
            {/* Enlace a Bandeja con badge de waiting_human */}
            <Link
              to="/inbox"
              className="relative flex items-center gap-2 rounded-lg bg-indigo-50 hover:bg-indigo-100
                         text-indigo-700 text-xs font-semibold px-3 py-1.5 transition-colors"
            >
              💬 Bandeja
              {(summary.data?.pendingHumanHandoff ?? 0) > 0 && (
                <span className="bg-amber-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">
                  {summary.data!.pendingHumanHandoff}
                </span>
              )}
            </Link>
            <span className="text-xs text-gray-400">
              {filters.dateFrom} → {filters.dateTo}
            </span>
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 sm:px-6 py-6 space-y-6">

        {/* ── Filtros ──────────────────────────────────────────────────────── */}
        <section className="bg-white rounded-xl border border-gray-100 shadow-sm p-4">
          <form onSubmit={applyFilters} className="flex flex-wrap gap-3 items-end">
            <div className="flex flex-col gap-1">
              <label className="text-xs font-medium text-gray-600">Desde</label>
              <input
                type="date"
                name="dateFrom"
                defaultValue={filters.dateFrom}
                className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
              />
            </div>
            <div className="flex flex-col gap-1">
              <label className="text-xs font-medium text-gray-600">Hasta</label>
              <input
                type="date"
                name="dateTo"
                defaultValue={filters.dateTo}
                className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
              />
            </div>
            <div className="flex flex-col gap-1">
              <label className="text-xs font-medium text-gray-600">Flujo</label>
              <select
                name="flowId"
                defaultValue=""
                className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-indigo-400"
              >
                <option value="">Todos los flujos</option>
                <option value="flow_00">flow_00 · General</option>
                <option value="flow_01">flow_01 · Llamada perdida</option>
                <option value="flow_02">flow_02 · Huecos</option>
                <option value="flow_03">flow_03 · Recordatorio</option>
                <option value="flow_04">flow_04 · No-show</option>
                <option value="flow_05">flow_05 · Lista espera</option>
                <option value="flow_06">flow_06 · Reactivación</option>
                <option value="flow_07">flow_07 · Reprogramación</option>
              </select>
            </div>
            <button
              type="submit"
              className="rounded-lg bg-indigo-600 text-white px-4 py-1.5 text-sm font-medium hover:bg-indigo-700 transition-colors"
            >
              Aplicar
            </button>
          </form>
        </section>

        {/* ── Alertas ───────────────────────────────────────────────────────── */}
        {(hasHumanAlert || hasFailedAlert) && (
          <div className="flex flex-col sm:flex-row gap-3">
            {hasHumanAlert && (
              <div className="flex-1 flex items-center gap-3 rounded-xl bg-amber-50 border border-amber-200 p-4">
                <span className="text-2xl">🙋</span>
                <div>
                  <p className="text-sm font-semibold text-amber-800">
                    {summary.data?.pendingHumanHandoff} conversación{summary.data!.pendingHumanHandoff > 1 ? 'es' : ''} esperando atención humana
                  </p>
                  <div className="flex items-center gap-3 mt-1">
                    <button
                      onClick={() => { setHumanOnly(true); setConvPage(1) }}
                      className="text-xs text-amber-700 underline hover:no-underline"
                    >
                      Ver en lista →
                    </button>
                    <Link
                      to="/inbox?status=waiting_human"
                      className="text-xs text-amber-700 font-semibold underline hover:no-underline"
                    >
                      Abrir Bandeja →
                    </Link>
                  </div>
                </div>
              </div>
            )}
            {hasFailedAlert && (
              <div className="flex-1 flex items-center gap-3 rounded-xl bg-red-50 border border-red-200 p-4">
                <span className="text-2xl">⚠️</span>
                <p className="text-sm font-semibold text-red-800">
                  {summary.data?.messagesFailed} mensaje{summary.data!.messagesFailed > 1 ? 's' : ''} fallido{summary.data!.messagesFailed > 1 ? 's' : ''} en el periodo
                </p>
              </div>
            )}
          </div>
        )}

        {/* ── KPI Cards ─────────────────────────────────────────────────────── */}
        {summary.isLoading ? (
          <Spinner />
        ) : summary.isError ? (
          <ErrorBanner message="No se pudo cargar el resumen. ¿Está la API arrancada?" />
        ) : (
          <section className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-4">
            <KpiCard
              icon="💶"
              label="Revenue recuperado"
              value={fmtEur(summary.data?.totalRecoveredRevenue)}
              accent="green"
            />
            <KpiCard
              icon="📅"
              label="Citas recuperadas"
              value={fmt(summary.data?.recoveredAppointments)}
              accent="blue"
            />
            <KpiCard
              icon="💬"
              label="Conversaciones activas"
              value={fmt(summary.data?.activeConversations)}
              sub={summary.data?.pendingHumanHandoff ? `${summary.data.pendingHumanHandoff} esperan intervención` : undefined}
              accent={summary.data?.pendingHumanHandoff ? 'yellow' : 'blue'}
            />
            <KpiCard
              icon="📤"
              label="Mensajes enviados"
              value={fmt(summary.data?.messagesSent)}
              sub={`${fmt(summary.data?.deliveryRate, 1)}% entregados`}
              accent="purple"
            />
            <KpiCard
              icon="👁️"
              label="Tasa de lectura"
              value={`${fmt(summary.data?.readRate, 1)}%`}
              sub={`${fmt(summary.data?.messagesRead)} leídos de ${fmt(summary.data?.messagesDelivered)} entregados`}
              accent={summary.data?.readRate && summary.data.readRate > 50 ? 'green' : 'yellow'}
            />
          </section>
        )}

        {/* ── Gráfico de entregabilidad ─────────────────────────────────────── */}
        <section className="bg-white rounded-xl border border-gray-100 shadow-sm p-5">
          <h2 className="text-sm font-semibold text-gray-700 mb-4">
            Entregabilidad diaria
          </h2>
          {delivery.isLoading ? <Spinner /> :
           delivery.isError   ? <ErrorBanner message="Error cargando entregabilidad." /> :
           <DeliveryBarChart data={chartData} />}
        </section>

        {/* ── Tabla de flujos ───────────────────────────────────────────────── */}
        <section className="bg-white rounded-xl border border-gray-100 shadow-sm overflow-hidden">
          <div className="px-5 py-4 border-b border-gray-100">
            <h2 className="text-sm font-semibold text-gray-700">Rendimiento por flujo</h2>
          </div>
          {flowPerf.isLoading ? (
            <Spinner />
          ) : flowPerf.isError ? (
            <div className="p-5"><ErrorBanner message="Error cargando métricas de flujos." /></div>
          ) : (flowPerf.data?.flows.length ?? 0) === 0 ? (
            <p className="text-center text-gray-400 text-sm py-8">Sin datos en este periodo.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full text-sm">
                <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
                  <tr>
                    <th className="text-left px-5 py-3">Flujo</th>
                    <th className="text-right px-4 py-3">Enviados</th>
                    <th className="text-right px-4 py-3">Entregados</th>
                    <th className="text-right px-4 py-3">Leídos</th>
                    <th className="text-right px-4 py-3">Respuestas</th>
                    <th className="text-right px-4 py-3">Bookings</th>
                    <th className="text-right px-4 py-3">Conversión</th>
                    <th className="text-right px-4 py-3">Revenue</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-50">
                  {flowPerf.data!.flows.map(row => (
                    <tr key={row.flowId} className="hover:bg-gray-50/50 transition-colors">
                      <td className="px-5 py-3">
                        <div>
                          <span className="font-mono text-xs text-indigo-600 bg-indigo-50 px-1.5 py-0.5 rounded">
                            {row.flowId}
                          </span>
                          <span className="ml-2 text-gray-700">{row.flowLabel}</span>
                        </div>
                      </td>
                      <td className="text-right px-4 py-3 text-gray-700">{fmt(row.sent)}</td>
                      <td className="text-right px-4 py-3 text-gray-700">{fmt(row.delivered)}</td>
                      <td className="text-right px-4 py-3 text-gray-700">{fmt(row.read)}</td>
                      <td className="text-right px-4 py-3 text-gray-700">{fmt(row.replies)}</td>
                      <td className="text-right px-4 py-3 font-semibold text-green-700">{fmt(row.bookings)}</td>
                      <td className="text-right px-4 py-3">
                        <span className={`font-semibold ${row.conversionRate > 10 ? 'text-green-600' : 'text-gray-700'}`}>
                          {fmt(row.conversionRate, 1)}%
                        </span>
                      </td>
                      <td className="text-right px-4 py-3 font-semibold text-green-700">
                        {fmtEur(row.recoveredRevenue)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        {/* ── Conversaciones recientes ─────────────────────────────────────── */}
        <section className="bg-white rounded-xl border border-gray-100 shadow-sm overflow-hidden">
          <div className="px-5 py-4 border-b border-gray-100 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-gray-700">
              Conversaciones recientes
              {convs.data && (
                <span className="ml-2 text-xs font-normal text-gray-400">
                  ({convs.data.totalCount} total)
                </span>
              )}
            </h2>
            <div className="flex items-center gap-3">
              <label className="flex items-center gap-2 text-xs text-gray-600 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={humanOnly}
                  onChange={e => { setHumanOnly(e.target.checked); setConvPage(1) }}
                  className="rounded"
                />
                Solo esperan humano
              </label>
              <Link
                to="/inbox"
                className="text-xs text-indigo-600 hover:underline font-medium"
              >
                Ver bandeja →
              </Link>
            </div>
          </div>

          {convs.isLoading ? (
            <Spinner />
          ) : convs.isError ? (
            <div className="p-5"><ErrorBanner message="Error cargando conversaciones." /></div>
          ) : (convs.data?.items.length ?? 0) === 0 ? (
            <p className="text-center text-gray-400 text-sm py-8">Sin conversaciones en este periodo.</p>
          ) : (
            <>
              <div className="divide-y divide-gray-50">
                {convs.data!.items.map(c => (
                  <div
                    key={c.conversationId}
                    className="px-5 py-3 hover:bg-gray-50/50 transition-colors"
                  >
                    <div className="flex items-start justify-between gap-3">
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="font-medium text-sm text-gray-900 truncate">
                            {c.patientName}
                          </span>
                          <span className="text-xs text-gray-400">{c.patientPhone}</span>
                          {c.requiresHuman && (
                            <span className="inline-flex items-center gap-1 text-xs bg-amber-100 text-amber-700 px-1.5 py-0.5 rounded-full font-medium">
                              🙋 Handoff
                            </span>
                          )}
                        </div>
                        {c.lastMessagePreview && (
                          <p className="mt-0.5 text-xs text-gray-500 truncate">
                            {c.lastDirection === 'inbound' ? '← ' : '→ '}
                            {c.lastMessagePreview}
                          </p>
                        )}
                      </div>

                      <div className="flex flex-col items-end gap-1 shrink-0">
                        <div className="flex items-center gap-1.5">
                          <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium ${statusBadge(c.status)}`}>
                            {c.status.replace('_', ' ')}
                          </span>
                          <span className={`text-xs px-1.5 py-0.5 rounded-full ${deliveryBadge(c.lastDeliveryStatus)}`}>
                            {c.lastDeliveryStatus || '—'}
                          </span>
                        </div>
                        <span className="text-xs text-gray-400">
                          {relativeTime(c.updatedAt)} · {c.flowId}
                        </span>
                      </div>
                    </div>
                  </div>
                ))}
              </div>

              {/* Paginación */}
              {(convs.data!.totalCount > 15) && (
                <div className="px-5 py-3 border-t border-gray-100 flex items-center justify-between text-xs text-gray-500">
                  <span>
                    Página {convPage} · {convs.data!.totalCount} conversaciones
                  </span>
                  <div className="flex gap-2">
                    <button
                      disabled={convPage <= 1}
                      onClick={() => setConvPage(p => p - 1)}
                      className="px-3 py-1 rounded border border-gray-200 hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed"
                    >
                      ← Anterior
                    </button>
                    <button
                      disabled={!convs.data?.hasMore}
                      onClick={() => setConvPage(p => p + 1)}
                      className="px-3 py-1 rounded border border-gray-200 hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed"
                    >
                      Siguiente →
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </section>

        {/* ── Revenue Overview ──────────────────────────────────────────────── */}
        <section className="grid grid-cols-1 lg:grid-cols-2 gap-6">

          {/* Resumen económico */}
          <div className="bg-white rounded-xl border border-gray-100 shadow-sm p-5">
            <h2 className="text-sm font-semibold text-gray-700 mb-4">Resumen económico</h2>
            {revenue.isLoading ? <Spinner /> :
             revenue.isError   ? <ErrorBanner message="Error cargando revenue." /> : (
              <div className="space-y-3">
                <div className="flex justify-between items-center py-2 border-b border-gray-50">
                  <span className="text-sm text-gray-600">Revenue total recuperado</span>
                  <span className="font-bold text-lg text-green-700">{fmtEur(revenue.data?.totalRevenue)}</span>
                </div>
                <div className="flex justify-between items-center py-2 border-b border-gray-50">
                  <span className="text-sm text-gray-600">Success fee total (15%)</span>
                  <span className="font-semibold text-indigo-700">{fmtEur(revenue.data?.totalSuccessFee)}</span>
                </div>
                <div className="flex justify-between items-center py-2">
                  <span className="text-sm text-gray-600">Eventos de revenue</span>
                  <span className="font-semibold text-gray-700">{fmt(revenue.data?.totalEvents)}</span>
                </div>

                {(revenue.data?.byEventType.length ?? 0) > 0 && (
                  <div className="mt-3 pt-3 border-t border-gray-100">
                    <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Por tipo de evento</p>
                    <div className="space-y-1.5">
                      {revenue.data!.byEventType.map(et => (
                        <div key={et.eventType} className="flex justify-between text-sm">
                          <span className="text-gray-600 font-mono text-xs">{et.eventType}</span>
                          <span className="text-gray-800 font-medium">
                            {fmtEur(et.totalAmount)}
                            <span className="text-gray-400 font-normal ml-1">({et.count})</span>
                          </span>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>

          {/* Serie diaria de revenue */}
          <div className="bg-white rounded-xl border border-gray-100 shadow-sm p-5">
            <h2 className="text-sm font-semibold text-gray-700 mb-4">Revenue diario</h2>
            {revenue.isLoading ? <Spinner /> :
             revenue.isError   ? <ErrorBanner message="Error cargando revenue." /> :
             (revenue.data?.byDay.length ?? 0) === 0 ? (
              <p className="text-center text-gray-400 text-sm py-8">Sin eventos de revenue en este periodo.</p>
             ) : (
              <RevenueLineChart data={revenue.data!.byDay} />
            )}
          </div>
        </section>

        {/* ── Footer ────────────────────────────────────────────────────────── */}
        <footer className="text-center text-xs text-gray-400 py-4">
          ClinicBoost Dashboard MVP · datos en UTC · filtros aplicados: {filters.dateFrom} → {filters.dateTo}
        </footer>

      </main>
    </div>
  )
}

// ─── Gráfico de línea SVG (revenue diario) ───────────────────────────────────

interface RevenuePoint { date: string; amount: number; count: number }

function RevenueLineChart({ data }: { data: RevenuePoint[] }) {
  const W = 500, H = 120, PAD = 20
  const maxAmt = Math.max(...data.map(d => d.amount), 1)

  const pts = data.map((d, i) => ({
    x: PAD + (i / Math.max(data.length - 1, 1)) * (W - PAD * 2),
    y: H - PAD - (d.amount / maxAmt) * (H - PAD * 2),
    label: d.date.slice(5),
    amount: d.amount,
  }))

  const pathD = pts.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.x} ${p.y}`).join(' ')
  const areaD = pts.length > 1
    ? `${pathD} L ${pts[pts.length - 1].x} ${H - PAD} L ${pts[0].x} ${H - PAD} Z`
    : ''

  return (
    <svg viewBox={`0 0 ${W} ${H + 20}`} className="w-full" style={{ height: H + 30 }}>
      {/* Área */}
      {areaD && <path d={areaD} fill="#d1fae5" opacity={0.6} />}
      {/* Línea */}
      {pts.length > 1 && <path d={pathD} stroke="#10b981" strokeWidth={2} fill="none" />}
      {/* Puntos */}
      {pts.map((p, i) => (
        <g key={i}>
          <circle cx={p.x} cy={p.y} r={4} fill="#10b981" />
          <title>{p.label}: {p.amount.toFixed(2)} €</title>
        </g>
      ))}
      {/* Eje X */}
      <line x1={PAD} y1={H - PAD} x2={W - PAD} y2={H - PAD} stroke="#e5e7eb" strokeWidth={1} />
      {/* Labels (máx 8 para no solapar) */}
      {pts.filter((_, i) => i % Math.ceil(pts.length / 8) === 0).map((p, i) => (
        <text key={i} x={p.x} y={H + 10} textAnchor="middle" fontSize={8} fill="#9ca3af">
          {p.label}
        </text>
      ))}
    </svg>
  )
}

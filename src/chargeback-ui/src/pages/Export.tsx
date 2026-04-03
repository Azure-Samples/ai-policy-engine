import { useState, useEffect, useMemo } from "react"
import { Download, FileSpreadsheet, AlertTriangle, ClipboardList, Zap } from "lucide-react"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "../components/ui/card"
import { Button } from "../components/ui/button"
import { fetchExportPeriods, downloadBillingSummary, downloadClientAudit, downloadRequestBilling, fetchPlans } from "../api"
import type { ExportPeriod, ExportClient, PlanData } from "../types"

const MONTH_NAMES = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December"
]

function formatPeriod(p: ExportPeriod): string {
  return `${MONTH_NAMES[p.month - 1]} ${p.year}`
}

function periodKey(p: ExportPeriod): string {
  return `${p.year}-${String(p.month).padStart(2, "0")}`
}

function isCurrentMonth(period: ExportPeriod, current: ExportPeriod): boolean {
  return period.year === current.year && period.month === current.month
}

export function Export() {
  const [periods, setPeriods] = useState<ExportPeriod[]>([])
  const [clients, setClients] = useState<ExportClient[]>([])
  const [currentPeriod, setCurrentPeriod] = useState<ExportPeriod>({ year: new Date().getFullYear(), month: new Date().getMonth() + 1 })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Billing summary state
  const [summaryPeriod, setSummaryPeriod] = useState<string>("")

  // Client audit state
  const [auditPeriod, setAuditPeriod] = useState<string>("")
  const [auditClient, setAuditClient] = useState<string>("")

  // Request billing export state
  const [requestBillingPeriod, setRequestBillingPeriod] = useState<string>("")
  const [plans, setPlans] = useState<PlanData[]>([])
  const hasMultiplierPlans = plans.some(p => p.useMultiplierBilling)

  useEffect(() => {
    Promise.all([
      fetchExportPeriods(),
      fetchPlans().catch(() => ({ plans: [] })),
    ])
      .then(([data, plansRes]) => {
        setPeriods(data.periods)
        setCurrentPeriod(data.currentPeriod)
        setClients(data.clients)
        setPlans(plansRes.plans ?? [])
        // Default to most recent complete month, or first available
        const defaultPeriod = data.periods.find(p => !isCurrentMonth(p, data.currentPeriod)) ?? data.periods[0]
        if (defaultPeriod) {
          const key = periodKey(defaultPeriod)
          setSummaryPeriod(key)
          setAuditPeriod(key)
          setRequestBillingPeriod(key)
        }
        if (data.clients.length > 0) {
          setAuditClient(`${data.clients[0].clientAppId}|${data.clients[0].tenantId}`)
        }
      })
      .catch(err => {
        // Don't show error for auth failures — the user may not have the export role
        const msg = err?.message ?? ""
        if (msg.includes("401") || msg.includes("Unauthorized")) {
          setError("You do not have the Chargeback.Export role required to access export data. Contact your administrator to assign this role.")
        } else if (msg.includes("403") || msg.includes("Forbidden")) {
          setError("Access denied. The Chargeback.Export role is required to export data.")
        } else {
          setError(msg)
        }
      })
      .finally(() => setLoading(false))
  }, [])

  const selectedSummaryPeriod = useMemo(() => {
    if (!summaryPeriod) return null
    const [y, m] = summaryPeriod.split("-").map(Number)
    return { year: y, month: m } as ExportPeriod
  }, [summaryPeriod])

  const selectedAuditPeriod = useMemo(() => {
    if (!auditPeriod) return null
    const [y, m] = auditPeriod.split("-").map(Number)
    return { year: y, month: m } as ExportPeriod
  }, [auditPeriod])

  const summaryIsCurrentMonth = selectedSummaryPeriod ? isCurrentMonth(selectedSummaryPeriod, currentPeriod) : false
  const auditIsCurrentMonth = selectedAuditPeriod ? isCurrentMonth(selectedAuditPeriod, currentPeriod) : false

  const selectedRequestBillingPeriod = useMemo(() => {
    if (!requestBillingPeriod) return null
    const [y, m] = requestBillingPeriod.split("-").map(Number)
    return { year: y, month: m } as ExportPeriod
  }, [requestBillingPeriod])
  const requestBillingIsCurrentMonth = selectedRequestBillingPeriod ? isCurrentMonth(selectedRequestBillingPeriod, currentPeriod) : false

  const [downloading, setDownloading] = useState(false)
  const [downloadError, setDownloadError] = useState<string | null>(null)

  const handleBillingSummaryExport = async () => {
    if (!selectedSummaryPeriod) return
    setDownloading(true)
    setDownloadError(null)
    try {
      await downloadBillingSummary(selectedSummaryPeriod.year, selectedSummaryPeriod.month)
    } catch (err: any) {
      setDownloadError(err?.message ?? "Download failed")
    } finally {
      setDownloading(false)
    }
  }

  const handleClientAuditExport = async () => {
    if (!selectedAuditPeriod || !auditClient) return
    const [clientAppId, tenantId] = auditClient.split("|")
    if (!clientAppId || !tenantId) return
    setDownloading(true)
    setDownloadError(null)
    try {
      await downloadClientAudit(clientAppId, tenantId, selectedAuditPeriod.year, selectedAuditPeriod.month)
    } catch (err: any) {
      setDownloadError(err?.message ?? "Download failed")
    } finally {
      setDownloading(false)
    }
  }

  const handleRequestBillingExport = async () => {
    if (!selectedRequestBillingPeriod) return
    setDownloading(true)
    setDownloadError(null)
    try {
      await downloadRequestBilling(selectedRequestBillingPeriod.year, selectedRequestBillingPeriod.month)
    } catch (err: unknown) {
      setDownloadError(err instanceof Error ? err.message : "Download failed")
    } finally {
      setDownloading(false)
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <FileSpreadsheet className="h-6 w-6 text-[#0078D4]" />
        <h2 className="text-xl font-bold">Export Data</h2>
      </div>

      {error && (
        <div className="rounded-md bg-red-50 p-4 text-red-700 text-sm">
          {error}
        </div>
      )}

      {downloadError && (
        <div className="rounded-md bg-red-50 p-4 text-red-700 text-sm">
          {downloadError}
        </div>
      )}

      {loading ? (
        <div className="text-muted-foreground text-sm">Loading available periods…</div>
      ) : error ? null : periods.length === 0 ? (
        <div className="text-muted-foreground text-sm">No billing data available for export yet.</div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2">
          {/* Billing Summary Card */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <FileSpreadsheet className="h-4 w-4" />
                Billing Summary
              </CardTitle>
              <CardDescription>
                Download a rolled-up billing summary for all clients in a billing period.
                Includes total tokens, costs, and overbilling indicators per client and deployment.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div>
                <label htmlFor="summary-period" className="block text-sm font-medium mb-1">
                  Billing Period
                </label>
                <select
                  id="summary-period"
                  value={summaryPeriod}
                  onChange={e => setSummaryPeriod(e.target.value)}
                  className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                >
                  {periods.map(p => (
                    <option key={periodKey(p)} value={periodKey(p)}>
                      {formatPeriod(p)}
                      {isCurrentMonth(p, currentPeriod) ? " (current)" : ""}
                    </option>
                  ))}
                </select>
              </div>

              {summaryIsCurrentMonth && (
                <div className="flex items-start gap-2 rounded-md bg-amber-50 p-3 text-amber-800 text-sm">
                  <AlertTriangle className="h-4 w-4 mt-0.5 shrink-0" />
                  <span>
                    Data for {selectedSummaryPeriod ? formatPeriod(selectedSummaryPeriod) : ""} is incomplete — the current month has not ended.
                  </span>
                </div>
              )}

              <Button onClick={handleBillingSummaryExport} className="gap-2" disabled={!selectedSummaryPeriod || downloading}>
                <Download className="h-4 w-4" />
                Export Billing Summary
              </Button>
            </CardContent>
          </Card>

          {/* Client Audit Card */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <ClipboardList className="h-4 w-4" />
                Client Audit Trail
              </CardTitle>
              <CardDescription>
                Download a detailed audit log of every request for a specific client
                in a billing period. Use for financial reconciliation and compliance.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div>
                <label htmlFor="audit-period" className="block text-sm font-medium mb-1">
                  Billing Period
                </label>
                <select
                  id="audit-period"
                  value={auditPeriod}
                  onChange={e => setAuditPeriod(e.target.value)}
                  className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                >
                  {periods.map(p => (
                    <option key={periodKey(p)} value={periodKey(p)}>
                      {formatPeriod(p)}
                      {isCurrentMonth(p, currentPeriod) ? " (current)" : ""}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label htmlFor="audit-client" className="block text-sm font-medium mb-1">
                  Client
                </label>
                <select
                  id="audit-client"
                  value={auditClient}
                  onChange={e => setAuditClient(e.target.value)}
                  className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                >
                  {clients.length === 0 ? (
                    <option value="">No clients available</option>
                  ) : (
                    clients.map(c => (
                      <option key={`${c.clientAppId}|${c.tenantId}`} value={`${c.clientAppId}|${c.tenantId}`}>
                        {c.displayName ? `${c.displayName} (${c.clientAppId} / ${c.tenantId})` : `${c.clientAppId} / ${c.tenantId}`}
                      </option>
                    ))
                  )}
                </select>
              </div>

              {auditIsCurrentMonth && (
                <div className="flex items-start gap-2 rounded-md bg-amber-50 p-3 text-amber-800 text-sm">
                  <AlertTriangle className="h-4 w-4 mt-0.5 shrink-0" />
                  <span>
                    Data for {selectedAuditPeriod ? formatPeriod(selectedAuditPeriod) : ""} is incomplete — the current month has not ended.
                  </span>
                </div>
              )}

              <Button
                onClick={handleClientAuditExport}
                className="gap-2"
                disabled={!selectedAuditPeriod || !auditClient || downloading}
              >
                <Download className="h-4 w-4" />
                Export Client Audit
              </Button>
            </CardContent>
          </Card>

          {/* Request Billing Export Card — adaptive: only shown when multiplier billing plans exist */}
          {hasMultiplierPlans && (
            <Card className="md:col-span-2">
              <CardHeader>
                <CardTitle className="text-base flex items-center gap-2">
                  <Zap className="h-4 w-4" />
                  Request Billing Export
                </CardTitle>
                <CardDescription>
                  Download a request-based billing report with multiplier costs, effective requests,
                  and tier breakdowns for all clients using multiplier billing.
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <label htmlFor="request-billing-period" className="block text-sm font-medium mb-1">
                    Billing Period
                  </label>
                  <select
                    id="request-billing-period"
                    value={requestBillingPeriod}
                    onChange={e => setRequestBillingPeriod(e.target.value)}
                    className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                  >
                    {periods.map(p => (
                      <option key={periodKey(p)} value={periodKey(p)}>
                        {formatPeriod(p)}
                        {isCurrentMonth(p, currentPeriod) ? " (current)" : ""}
                      </option>
                    ))}
                  </select>
                </div>

                {requestBillingIsCurrentMonth && (
                  <div className="flex items-start gap-2 rounded-md bg-amber-50 p-3 text-amber-800 text-sm">
                    <AlertTriangle className="h-4 w-4 mt-0.5 shrink-0" />
                    <span>
                      Data for {selectedRequestBillingPeriod ? formatPeriod(selectedRequestBillingPeriod) : ""} is incomplete — the current month has not ended.
                    </span>
                  </div>
                )}

                <Button
                  onClick={handleRequestBillingExport}
                  className="gap-2"
                  disabled={!selectedRequestBillingPeriod || downloading}
                >
                  <Download className="h-4 w-4" />
                  Export Request Billing
                </Button>
              </CardContent>
            </Card>
          )}
        </div>
      )}
    </div>
  )
}

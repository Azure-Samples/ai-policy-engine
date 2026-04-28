import { useEffect, useState, useCallback, useMemo } from "react"
import { fetchRequestSummary, fetchPlans } from "../api"
import type { RequestSummaryResponse, PlanData } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Badge } from "../components/ui/badge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { BarChart3, AlertTriangle, TrendingUp, Users, Zap } from "lucide-react"
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell, Legend,
} from "recharts"
import { useTheme } from "../context/ThemeProvider"

const TIER_COLORS: Record<string, string> = {
  Standard: "#0078D4",
  Premium: "#FFB900",
  Ultra: "#D13438",
  Enterprise: "#8764B8",
}
const PIE_COLORS = ["#0078D4", "#FFB900", "#D13438", "#8764B8", "#00B7C3", "#107C10"]

interface RequestBillingProps {
  onSelectClient?: (clientAppId: string, tenantId: string) => void
}

export function RequestBilling({ onSelectClient }: RequestBillingProps) {
  const [summary, setSummary] = useState<RequestSummaryResponse | null>(null)
  const [plans, setPlans] = useState<PlanData[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const { resolvedTheme } = useTheme()

  const chartTextColor = resolvedTheme === "dark" ? "#a39e99" : "#71706e"
  const gridColor = resolvedTheme === "dark" ? "#3b3a39" : "#e1dfdd"

  const loadData = useCallback(async () => {
    try {
      const [summaryRes, plansRes] = await Promise.all([
        fetchRequestSummary(),
        fetchPlans().catch(() => ({ plans: [] })),
      ])
      setSummary(summaryRes)
      setPlans(plansRes.plans ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load request billing data")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { loadData() }, [loadData])

  // Tier breakdown for pie chart
  const tierData = useMemo(() => {
    if (!summary?.totals?.effectiveRequestsByTier) return []
    return Object.entries(summary.totals.effectiveRequestsByTier)
      .filter(([, count]) => count > 0)
      .map(([tier, count]) => ({ name: tier, value: count }))
  }, [summary])

  // Per-client bar chart data
  const clientChartData = useMemo(() => {
    if (!summary?.clients) return []
    return summary.clients
      .sort((a, b) => b.totalEffectiveRequests - a.totalEffectiveRequests)
      .slice(0, 10)
      .map(c => ({
        name: c.displayName || c.clientAppId.substring(0, 12),
        effectiveRequests: c.totalEffectiveRequests,
        overageCost: c.multiplierOverageCost,
      }))
  }, [summary])

  // Clients with overage costs
  const overageAlerts = useMemo(() => {
    if (!summary?.clients) return []
    return summary.clients
      .filter(c => c.multiplierOverageCost > 0)
      .sort((a, b) => b.multiplierOverageCost - a.multiplierOverageCost)
  }, [summary])

  // Check if this page should even show
  const hasMultiplierPlans = plans.some(p => p.useMultiplierBilling)

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <BarChart3 className="h-8 w-8 text-[#0078D4] animate-pulse" />
        <span className="ml-2 text-muted-foreground">Loading request billing data…</span>
      </div>
    )
  }

  if (!hasMultiplierPlans && plans.length > 0) {
    return (
      <div className="space-y-6">
        <div className="flex items-center gap-3">
          <BarChart3 className="h-7 w-7 text-[#0078D4]" />
          <h2 className="text-2xl font-bold tracking-tight">Request Billing</h2>
        </div>
        <Card>
          <CardContent className="py-12 text-center text-muted-foreground">
            <BarChart3 className="h-10 w-10 mx-auto mb-3 opacity-40" />
            <p>No plans are configured for multiplier billing.</p>
            <p className="text-sm mt-1">Enable "Use Multiplier Billing" on a plan to see request-based billing data.</p>
          </CardContent>
        </Card>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center gap-3">
        <BarChart3 className="h-7 w-7 text-[#0078D4]" />
        <div>
          <h2 className="text-2xl font-bold tracking-tight">Request Billing</h2>
          <p className="text-sm text-muted-foreground">
            Effective request consumption and multiplier-based billing across all clients.
            {summary?.billingPeriod && (
              <span className="ml-1">
                Period: {summary.billingPeriod}
              </span>
            )}
          </p>
        </div>
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive flex items-center gap-2">
          <AlertTriangle className="h-4 w-4" />
          {error}
        </div>
      )}

      {/* KPI Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Total Requests</CardTitle>
            <Zap className="h-4 w-4 text-[#0078D4]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">
              {(summary?.totals?.totalRawRequests ?? 0).toLocaleString()}
            </div>
            <p className="text-xs text-muted-foreground mt-1">Raw requests this period</p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Effective Requests</CardTitle>
            <TrendingUp className="h-4 w-4 text-[#00B7C3]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">
              {(summary?.totals?.totalEffectiveRequests ?? 0).toLocaleString()}
            </div>
            <p className="text-xs text-muted-foreground mt-1">After multiplier applied</p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Overage Cost</CardTitle>
            <AlertTriangle className="h-4 w-4 text-[#D13438]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">
              ${(summary?.totals?.totalMultiplierOverageCost ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
            </div>
            <p className="text-xs text-muted-foreground mt-1">Multiplier overage cost</p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Active Clients</CardTitle>
            <Users className="h-4 w-4 text-[#107C10]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">
              {(summary?.clients?.length ?? 0)}
            </div>
            <p className="text-xs text-muted-foreground mt-1">With request activity</p>
          </CardContent>
        </Card>
      </div>

      {/* Charts Row */}
      <div className="grid gap-6 lg:grid-cols-2">
        {/* Effective Request Consumption */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Effective Requests by Client</CardTitle>
          </CardHeader>
          <CardContent>
            {clientChartData.length === 0 ? (
              <div className="text-center py-8 text-muted-foreground text-sm">No request data available.</div>
            ) : (
              <ResponsiveContainer width="100%" height={300}>
                <BarChart data={clientChartData}>
                  <CartesianGrid strokeDasharray="3 3" stroke={gridColor} />
                  <XAxis dataKey="name" tick={{ fill: chartTextColor, fontSize: 12 }} angle={-20} textAnchor="end" height={60} />
                  <YAxis tick={{ fill: chartTextColor, fontSize: 12 }} />
                  <Tooltip
                    contentStyle={{
                      backgroundColor: resolvedTheme === "dark" ? "#292827" : "#fff",
                      border: `1px solid ${gridColor}`,
                      borderRadius: "8px",
                      color: resolvedTheme === "dark" ? "#e1dfdd" : "#323130",
                    }}
                  />
                  <Bar dataKey="effectiveRequests" name="Effective Requests" fill="#0078D4" radius={[4, 4, 0, 0]} />
                  <Bar dataKey="overageCost" name="Overage Cost" fill="#D13438" radius={[4, 4, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>

        {/* Tier Breakdown Pie Chart */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Requests by Tier</CardTitle>
          </CardHeader>
          <CardContent>
            {tierData.length === 0 ? (
              <div className="text-center py-8 text-muted-foreground text-sm">No tier data available.</div>
            ) : (
              <ResponsiveContainer width="100%" height={300}>
                <PieChart>
                  <Pie
                    data={tierData}
                    cx="50%"
                    cy="50%"
                    innerRadius={60}
                    outerRadius={110}
                    paddingAngle={2}
                    dataKey="value"
                    label={({ name, percent }: { name?: string; percent?: number }) => `${name ?? ''} ${((percent ?? 0) * 100).toFixed(0)}%`}
                  >
                    {tierData.map((entry, i) => (
                      <Cell key={entry.name} fill={TIER_COLORS[entry.name] ?? PIE_COLORS[i % PIE_COLORS.length]} />
                    ))}
                  </Pie>
                  <Tooltip
                    contentStyle={{
                      backgroundColor: resolvedTheme === "dark" ? "#292827" : "#fff",
                      border: `1px solid ${gridColor}`,
                      borderRadius: "8px",
                      color: resolvedTheme === "dark" ? "#e1dfdd" : "#323130",
                    }}
                  />
                  <Legend />
                </PieChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>
      </div>

      {/* Overage Alerts */}
      {overageAlerts.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base flex items-center gap-2">
              <AlertTriangle className="h-4 w-4 text-[#D13438]" />
              Overage Alerts
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="space-y-3">
              {overageAlerts.map(c => (
                <div
                  key={`${c.clientAppId}-${c.tenantId}`}
                  className="flex items-center gap-4 rounded-lg border p-3"
                >
                  <div className="flex-1 min-w-0">
                    <button
                      className="font-medium text-sm text-[#0078D4] hover:underline cursor-pointer"
                      onClick={() => onSelectClient?.(c.clientAppId, c.tenantId)}
                    >
                      {c.displayName || c.clientAppId}
                    </button>
                    <p className="text-xs text-muted-foreground">{c.rawRequestCount.toLocaleString()} raw requests</p>
                  </div>
                  <span className="font-mono text-sm">
                    ${c.multiplierOverageCost.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                  </span>
                  <Badge variant="red">Overage</Badge>
                </div>
              ))}
            </div>
          </CardContent>
        </Card>
      )}

      {/* Per-Client Summary Table */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Client Summary</CardTitle>
        </CardHeader>
        <CardContent>
          {!summary?.clients || summary.clients.length === 0 ? (
            <div className="text-center py-8 text-muted-foreground text-sm">No client data available.</div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Client</TableHead>
                  <TableHead className="text-right">Raw Requests</TableHead>
                  <TableHead className="text-right">Effective</TableHead>
                  <TableHead className="text-right">Overage Cost</TableHead>
                  <TableHead>Tier Breakdown</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {summary.clients.map(c => (
                  <TableRow key={`${c.clientAppId}-${c.tenantId}`}>
                    <TableCell>
                      <button
                        className="font-mono text-xs text-[#0078D4] hover:underline cursor-pointer"
                        onClick={() => onSelectClient?.(c.clientAppId, c.tenantId)}
                      >
                        {c.displayName || c.clientAppId}
                      </button>
                    </TableCell>
                    <TableCell className="text-right font-mono text-sm">
                      {c.rawRequestCount.toLocaleString()}
                    </TableCell>
                    <TableCell className="text-right font-mono text-sm">
                      {c.totalEffectiveRequests.toLocaleString()}
                    </TableCell>
                    <TableCell className="text-right">
                      {c.multiplierOverageCost > 0 ? (
                        <Badge variant="red">${c.multiplierOverageCost.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</Badge>
                      ) : (
                        <span className="text-muted-foreground font-mono text-sm">$0.00</span>
                      )}
                    </TableCell>
                    <TableCell>
                      {c.effectiveRequestsByTier && Object.keys(c.effectiveRequestsByTier).length > 0 ? (
                        <div className="flex flex-wrap gap-1">
                          {Object.entries(c.effectiveRequestsByTier).map(([tier, count]) => (
                            <Badge key={tier} variant="secondary" className="text-xs">
                              {tier}: {count.toLocaleString()}
                            </Badge>
                          ))}
                        </div>
                      ) : (
                        <span className="text-muted-foreground text-xs">—</span>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

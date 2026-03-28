import { useEffect, useState, useCallback } from "react"
import { fetchClientUsage, fetchClientTraces } from "../api"
import type { ClientUsageResponse, TraceRecord } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Badge } from "../components/ui/badge"
import { Button } from "../components/ui/button"
import { Progress } from "../components/ui/progress"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { ArrowLeft, DollarSign, TrendingUp, Coins, AlertTriangle, Gauge } from "lucide-react"
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Cell, LineChart, Line, Legend } from "recharts"
import { useTheme } from "../context/ThemeProvider"

const BAR_COLORS = ["#0078D4", "#00B7C3", "#107C10", "#FFB900", "#D13438", "#8764B8", "#005A9E", "#106EBE"]

interface ClientDetailProps {
  clientAppId: string
  tenantId: string
  onBack: () => void
}

export function ClientDetail({ clientAppId, tenantId, onBack }: ClientDetailProps) {
  const [usage, setUsage] = useState<ClientUsageResponse | null>(null)
  const [traces, setTraces] = useState<TraceRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const { resolvedTheme } = useTheme()

  const loadData = useCallback(async () => {
    try {
      const [usageRes, tracesRes] = await Promise.all([
        fetchClientUsage(clientAppId, tenantId),
        fetchClientTraces(clientAppId, tenantId),
      ])
      setUsage(usageRes)
      setTraces(tracesRes.traces ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load client data")
    } finally {
      setLoading(false)
    }
  }, [clientAppId, tenantId])

  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 5000)
    return () => clearInterval(interval)
  }, [loadData])

  const chartTextColor = resolvedTheme === "dark" ? "#a39e99" : "#71706e"
  const gridColor = resolvedTheme === "dark" ? "#3b3a39" : "#e1dfdd"

  // Derived values
  const assignment = usage?.assignment
  const plan = usage?.plan
  const quota = plan?.monthlyTokenQuota ?? 0
  const currentUsage = assignment?.currentPeriodUsage ?? 0
  const quotaPct = quota > 0 ? (currentUsage / quota) * 100 : 0
  const overbilled = assignment?.overbilledTokens ?? 0
  const tpmLimit = plan?.tokensPerMinuteLimit ?? 0
  const rpmLimit = plan?.requestsPerMinuteLimit ?? 0
  const currentTpm = usage?.currentTpm ?? 0
  const currentRpm = usage?.currentRpm ?? 0

  // Chart data
  const modelChartData = Object.entries(usage?.usageByModel ?? {}).map(([model, tokens]) => ({
    model,
    tokens,
  }))

  // Sort traces newest first
  const sortedTraces = [...traces].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())
  const fiveMinuteUtilization = (() => {
    const bucketMs = 10_000
    const bucketCount = 30
    const currentBucketStart = Math.floor(Date.now() / bucketMs) * bucketMs
    const windowStart = currentBucketStart - (bucketCount - 1) * bucketMs
    const perMinuteScale = 60_000 / bucketMs

    const buckets = Array.from({ length: bucketCount }, (_, i) => {
      const bucketStart = windowStart + i * bucketMs
      const bucketTime = new Date(bucketStart)
      return {
        bucketStart,
        timeLabel: bucketTime.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }),
        requests: 0,
        tokens: 0,
      }
    })

    for (const trace of traces) {
      const ts = new Date(trace.timestamp).getTime()
      if (Number.isNaN(ts) || ts < windowStart || ts >= currentBucketStart + bucketMs) continue

      const bucketIndex = Math.floor((ts - windowStart) / bucketMs)
      if (bucketIndex < 0 || bucketIndex >= buckets.length) continue

      buckets[bucketIndex].requests += 1
      buckets[bucketIndex].tokens += trace.totalTokens
    }

    return buckets.map((bucket) => ({
      timeLabel: bucket.timeLabel,
      requests: bucket.requests,
      tokens: bucket.tokens,
      tpmUtilizationPct: tpmLimit > 0 ? ((bucket.tokens * perMinuteScale) / tpmLimit) * 100 : 0,
      rpmUtilizationPct: rpmLimit > 0 ? ((bucket.requests * perMinuteScale) / rpmLimit) * 100 : 0,
    }))
  })()
  const peakTpmUtilizationPct = Math.max(0, ...fiveMinuteUtilization.map((bucket) => bucket.tpmUtilizationPct))
  const peakRpmUtilizationPct = Math.max(0, ...fiveMinuteUtilization.map((bucket) => bucket.rpmUtilizationPct))

  function formatTimestamp(ts: string): string {
    try {
      return new Date(ts).toLocaleString()
    } catch {
      return ts
    }
  }

  function getStatusBadge(trace: TraceRecord) {
    if (trace.isOverbilled) return <Badge variant="amber">Overbilled</Badge>
    if (trace.statusCode === 429) return <Badge variant="red">Blocked</Badge>
    if (trace.statusCode >= 200 && trace.statusCode < 300) return <Badge variant="green">OK</Badge>
    return <Badge variant="secondary">{trace.statusCode}</Badge>
  }

  if (error && !usage) {
    return (
      <div className="space-y-4">
        <Button variant="ghost" className="gap-2" onClick={onBack}>
          <ArrowLeft className="h-4 w-4" /> Back to Dashboard
        </Button>
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-destructive">
          Error: {error}
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center gap-4">
        <Button variant="ghost" className="gap-2" onClick={onBack}>
          <ArrowLeft className="h-4 w-4" /> Back to Dashboard
        </Button>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        <h2 className="text-xl font-bold">Client Detail</h2>
        <code className="text-sm font-mono bg-muted px-2 py-1 rounded">{clientAppId}</code>
        <code className="text-sm font-mono bg-muted px-2 py-1 rounded text-muted-foreground">{tenantId}</code>
        {assignment?.displayName && (
          <span className="text-muted-foreground">— {assignment.displayName}</span>
        )}
        {plan && <Badge variant="blue">{plan.name}</Badge>}
      </div>

      {/* Live indicator */}
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <span className="relative flex h-2.5 w-2.5">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-green-400 opacity-75" />
          <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-green-500" />
        </span>
        Live — refreshing every 5s
        {loading && <span className="text-xs">(updating…)</span>}
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-destructive text-sm">
          {error}
        </div>
      )}

      {/* KPI Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-5">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Quota Usage</CardTitle>
            <Coins className="h-4 w-4 text-[#0078D4]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">{quotaPct.toFixed(1)}%</div>
            <Progress value={currentUsage} max={quota || 1} className="mt-2" />
            <p className="text-xs text-muted-foreground mt-1 font-mono">
              {currentUsage.toLocaleString()} / {quota.toLocaleString()} tokens
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Overbilled Tokens</CardTitle>
            <AlertTriangle className="h-4 w-4 text-[#D13438]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">{overbilled.toLocaleString()}</div>
            {overbilled > 0 && <Badge variant="red" className="mt-2">Overbilled</Badge>}
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Cost to Us</CardTitle>
            <DollarSign className="h-4 w-4 text-[#107C10]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">${(usage?.totalCostToUs ?? 0).toFixed(2)}</div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Customer Cost</CardTitle>
            <TrendingUp className="h-4 w-4 text-[#00B7C3]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">${(usage?.totalCostToCustomer ?? 0).toFixed(2)}</div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Rate Limits</CardTitle>
            <Gauge className="h-4 w-4 text-[#FFB900]" />
          </CardHeader>
          <CardContent>
            <div className="space-y-2">
              <div>
                <div className="flex justify-between text-xs text-muted-foreground mb-1">
                  <span>TPM</span>
                  <span className="font-mono">{currentTpm.toLocaleString()} / {tpmLimit.toLocaleString()}</span>
                </div>
                <Progress value={currentTpm} max={tpmLimit || 1} className="h-2" />
              </div>
              <div>
                <div className="flex justify-between text-xs text-muted-foreground mb-1">
                  <span>RPM</span>
                  <span className="font-mono">{currentRpm.toLocaleString()} / {rpmLimit.toLocaleString()}</span>
                </div>
                <Progress value={currentRpm} max={rpmLimit || 1} className="h-2" />
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <CardTitle className="text-base">Utilization Trend (Last 5 Minutes)</CardTitle>
          <span className="text-xs text-muted-foreground">
            Peak TPM {peakTpmUtilizationPct.toFixed(1)}% · Peak RPM {peakRpmUtilizationPct.toFixed(1)}%
          </span>
        </CardHeader>
        <CardContent className="h-[240px]">
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={fiveMinuteUtilization}>
              <CartesianGrid strokeDasharray="3 3" stroke={gridColor} />
              <XAxis dataKey="timeLabel" tick={{ fill: chartTextColor, fontSize: 11 }} interval={2} minTickGap={20} />
              <YAxis
                yAxisId="left"
                tick={{ fill: chartTextColor, fontSize: 12 }}
                tickFormatter={(v: number) => v.toLocaleString()}
              />
              <YAxis
                yAxisId="right"
                orientation="right"
                tick={{ fill: chartTextColor, fontSize: 12 }}
                allowDecimals={false}
              />
              <Tooltip
                contentStyle={{
                  backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff",
                  border: "1px solid #3b3a39",
                  borderRadius: 8,
                }}
                formatter={(value, name) => {
                  if (name === "tokens") return [(Number(value) || 0).toLocaleString(), "Tokens"]
                  return [Number(value) || 0, "Requests"]
                }}
              />
              <Legend />
              <Line yAxisId="left" type="monotone" dataKey="tokens" name="Tokens" stroke="#0078D4" strokeWidth={2} dot={{ r: 2 }} activeDot={{ r: 4 }} />
              <Line yAxisId="right" type="monotone" dataKey="requests" name="Requests" stroke="#00B7C3" strokeWidth={2} dot={{ r: 2 }} activeDot={{ r: 4 }} />
            </LineChart>
          </ResponsiveContainer>
        </CardContent>
      </Card>

      {/* Usage by Model Chart */}
      {modelChartData.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Usage by Model</CardTitle>
          </CardHeader>
          <CardContent className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={modelChartData} margin={{ left: 20 }}>
                <CartesianGrid strokeDasharray="3 3" stroke={gridColor} />
                <XAxis dataKey="model" tick={{ fill: chartTextColor, fontSize: 11 }} />
                <YAxis tick={{ fill: chartTextColor, fontSize: 12 }} tickFormatter={(v: number) => v.toLocaleString()} />
                <Tooltip
                  contentStyle={{
                    backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff",
                    border: "1px solid #3b3a39",
                    borderRadius: 8,
                  }}
                  formatter={(value) => [(Number(value) || 0).toLocaleString(), "Tokens"]}
                />
                <Bar dataKey="tokens" radius={[4, 4, 0, 0]}>
                  {modelChartData.map((_entry, i) => (
                    <Cell key={i} fill={BAR_COLORS[i % BAR_COLORS.length]} />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>
      )}

      {/* Request Traces Table */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Request Traces</CardTitle>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Timestamp</TableHead>
                <TableHead>Model</TableHead>
                <TableHead className="text-right">Prompt Tokens</TableHead>
                <TableHead className="text-right">Completion Tokens</TableHead>
                <TableHead className="text-right">Total Tokens</TableHead>
                <TableHead className="text-right">Our Cost</TableHead>
                <TableHead className="text-right">Customer Cost</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sortedTraces.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={8} className="text-center text-muted-foreground py-8">
                    No request traces found.
                  </TableCell>
                </TableRow>
              ) : (
                sortedTraces.map((trace, idx) => (
                  <TableRow key={`${trace.timestamp}-${idx}`}>
                    <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                      {formatTimestamp(trace.timestamp)}
                    </TableCell>
                    <TableCell>
                      <Badge variant="secondary">{trace.model ?? "unknown"}</Badge>
                    </TableCell>
                    <TableCell className="font-mono text-right">{trace.promptTokens.toLocaleString()}</TableCell>
                    <TableCell className="font-mono text-right">{trace.completionTokens.toLocaleString()}</TableCell>
                    <TableCell className="font-mono text-right">{trace.totalTokens.toLocaleString()}</TableCell>
                    <TableCell className="font-mono text-right">{trace.costToUs}</TableCell>
                    <TableCell className="font-mono text-right">{trace.costToCustomer}</TableCell>
                    <TableCell>{getStatusBadge(trace)}</TableCell>
                  </TableRow>
                ))
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  )
}

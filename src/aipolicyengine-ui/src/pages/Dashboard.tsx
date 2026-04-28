import { useEffect, useState, useCallback } from "react"
import { fetchChargeback, fetchClients, fetchPlans, fetchRequestLogs } from "../api"
import type { LogEntry, RequestLogEntry, ClientAssignment, PlanData } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Progress } from "../components/ui/progress"
import { Badge } from "../components/ui/badge"
import { Button } from "../components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { DollarSign, Coins, Users, Cpu, ArrowUpDown, TrendingUp, ScrollText } from "lucide-react"
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell,
  Legend, LineChart, Line,
} from "recharts"
import { useTheme } from "../context/ThemeProvider"

const MODEL_COLORS: Record<string, { variant: "blue" | "green" | "teal" | "amber" | "cyan" | "red"; hex: string }> = {
  "gpt-5.3-codex": { variant: "blue", hex: "#0078D4" },
  "gpt-5.2": { variant: "blue", hex: "#005A9E" },
  "gpt-4.1": { variant: "teal", hex: "#00B7C3" },
  "gpt-4.1-mini": { variant: "cyan", hex: "#00B7C3" },
  "gpt-4.1-nano": { variant: "green", hex: "#107C10" },
  "gpt-4o": { variant: "amber", hex: "#FFB900" },
  "gpt-4o-mini": { variant: "amber", hex: "#FFB900" },
  "gpt-4": { variant: "blue", hex: "#0078D4" },
  "gpt-oss-120b": { variant: "green", hex: "#107C10" },
  "dall-e-3": { variant: "red", hex: "#D13438" },
  "text-embedding-3-large": { variant: "amber", hex: "#FFB900" },
}

const PIE_COLORS = ["#0078D4", "#00B7C3", "#107C10", "#FFB900", "#D13438", "#8764B8", "#005A9E", "#106EBE"]
const MAX_CLIENT_OVERVIEW_CARDS = 4

function getModelStyle(model: string | null | undefined) {
  if (!model) return { variant: "secondary" as const, hex: "#6b7280" }
  const lower = model.toLowerCase()
  for (const key of Object.keys(MODEL_COLORS)) {
    if (lower.includes(key)) return MODEL_COLORS[key]
  }
  return { variant: "secondary" as const, hex: "#6b7280" }
}

const DEPLOYMENT_VARIANTS = ["blue", "teal", "green", "amber", "cyan", "red"] as const
const DEPLOYMENT_HEX: Record<(typeof DEPLOYMENT_VARIANTS)[number], string> = {
  blue: "#0078D4",
  teal: "#00B7C3",
  green: "#107C10",
  amber: "#FFB900",
  cyan: "#00B7C3",
  red: "#D13438",
}

function getDeploymentStyle(deploymentId: string | null | undefined) {
  if (!deploymentId) return { variant: "secondary" as const, hex: "#6b7280" }
  const modelStyle = getModelStyle(deploymentId)
  if (modelStyle.variant !== "secondary") return modelStyle

  const hash = Array.from(deploymentId).reduce((sum, char) => sum + char.charCodeAt(0), 0)
  const variant = DEPLOYMENT_VARIANTS[hash % DEPLOYMENT_VARIANTS.length]
  return { variant, hex: DEPLOYMENT_HEX[variant] }
}

type SortKey = keyof LogEntry
type SortDir = "asc" | "desc"

export function Dashboard({ onSelectClient }: { onSelectClient?: (clientAppId: string, tenantId: string) => void }) {
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [requestLogs, setRequestLogs] = useState<RequestLogEntry[]>([])
  const [clientNameMap, setClientNameMap] = useState<Map<string, string>>(new Map())
  const [clients, setClients] = useState<ClientAssignment[]>([])
  const [planMap, setPlanMap] = useState<Map<string, PlanData>>(new Map())
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [sortKey, setSortKey] = useState<SortKey>("totalCost")
  const [sortDir, setSortDir] = useState<SortDir>("desc")
  const [usagePage, setUsagePage] = useState(0)
  const USAGE_PAGE_SIZE = 10
  const { resolvedTheme } = useTheme()

  const loadData = useCallback(async () => {
    try {
      const [data, clientsRes, plansRes, reqLogs] = await Promise.all([
        fetchChargeback(),
        fetchClients(),
        fetchPlans(),
        fetchRequestLogs(),
      ])
      setLogs(data.logs ?? [])

      // Build client display name lookup
      const clientList = (clientsRes.clients ?? []) as ClientAssignment[]
      const nameMap = new Map<string, string>()
      for (const c of clientList) {
        if (c.displayName) nameMap.set(`${c.clientAppId}:${c.tenantId}`, c.displayName)
      }
      setClientNameMap(nameMap)
      setClients(clientList)

      // Build plan lookup
      const pMap = new Map<string, PlanData>()
      for (const p of (plansRes.plans ?? []) as PlanData[]) {
        pMap.set(p.id, p)
      }
      setPlanMap(pMap)

      setRequestLogs(reqLogs.entries ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load data")
    } finally {
      setLoading(false)
    }
  }, [])

  // Auto-refresh every 10s
  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 10000)
    return () => clearInterval(interval)
  }, [loadData])

  // KPI calculations
  const totalCostToUs = logs.reduce((s, e) => s + (parseFloat((e.costToUs ?? "0").replace(/[^0-9.-]/g, "")) || 0), 0)
  const totalRevenue = logs.reduce((s, e) => s + (parseFloat((e.costToCustomer ?? "0").replace(/[^0-9.-]/g, "")) || 0), 0)
  const totalTokens = logs.reduce((s, e) => s + e.totalTokens, 0)
  const uniqueClients = new Set(logs.map((e) => `${e.clientAppId}:${e.tenantId}`)).size
  const activeModels = new Set(logs.map((e) => e.model ?? "unknown")).size

  // Chart data: Cost by deployment
  const costByDeployment = Object.entries(
    logs.reduce<Record<string, number>>((acc, e) => {
      const deployment = e.deploymentId || "unknown"
      acc[deployment] = (acc[deployment] ?? 0) + (parseFloat(e.totalCost.replace(/[^0-9.-]/g, "")) || 0)
      return acc
    }, {})
  )
    .map(([deploymentId, cost]) => ({ deploymentId, cost: +cost.toFixed(4) }))
    .sort((a, b) => b.cost - a.cost)

  // Chart data: Tokens by client (use display names, keyed by customer)
  const tokensByClient = Object.entries(
    logs.reduce<Record<string, number>>((acc, e) => {
      const customerKey = `${e.clientAppId}:${e.tenantId}`
      const displayName = clientNameMap.get(customerKey) ?? e.clientAppId
      const name = displayName.length > 16 ? displayName.slice(0, 14) + "…" : displayName
      acc[name] = (acc[name] ?? 0) + e.totalTokens
      return acc
    }, {})
  ).map(([name, tokens]) => ({ name, tokens }))

  // Chart data: Usage trend (aggregate by deployment)
  const usageTrend = Object.entries(
    logs.reduce<Record<string, { prompt: number; completion: number }>>((acc, e) => {
      const key = e.deploymentId || "unknown"
      if (!acc[key]) acc[key] = { prompt: 0, completion: 0 }
      acc[key].prompt += e.promptTokens
      acc[key].completion += e.completionTokens
      return acc
    }, {})
  )
    .map(([deploymentId, data]) => ({ deploymentId, prompt: data.prompt, completion: data.completion }))
    .sort((a, b) => (b.prompt + b.completion) - (a.prompt + a.completion))

  const fiveMinuteUtilization = (() => {
    const bucketMs = 10_000
    const bucketCount = 30
    const currentBucketStart = Math.floor(Date.now() / bucketMs) * bucketMs
    const windowStart = currentBucketStart - (bucketCount - 1) * bucketMs

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

    for (const entry of requestLogs) {
      const ts = new Date(entry.timestamp).getTime()
      if (Number.isNaN(ts) || ts < windowStart || ts >= currentBucketStart + bucketMs) continue

      const bucketIndex = Math.floor((ts - windowStart) / bucketMs)
      if (bucketIndex < 0 || bucketIndex >= buckets.length) continue

      buckets[bucketIndex].requests += 1
      buckets[bucketIndex].tokens += entry.totalTokens
    }

    return buckets.map((bucket) => ({
      timeLabel: bucket.timeLabel,
      requests: bucket.requests,
      tokens: bucket.tokens,
    }))
  })()

  const recentRequestLogs = requestLogs.slice(0, 20)

  // Per-client cost aggregation for client overview cards
  const costByClient = new Map<string, { costToUs: number; costToCustomer: number }>()
  for (const entry of logs) {
    const customerKey = `${entry.clientAppId}:${entry.tenantId}`
    const existing = costByClient.get(customerKey) ?? { costToUs: 0, costToCustomer: 0 }
    existing.costToUs += parseFloat((entry.costToUs ?? "0").replace(/[^0-9.-]/g, "")) || 0
    existing.costToCustomer += parseFloat((entry.costToCustomer ?? "0").replace(/[^0-9.-]/g, "")) || 0
    costByClient.set(customerKey, existing)
  }

  const clientsByUtilization = [...clients].sort((a, b) => {
    const usageDelta = (b.currentPeriodUsage ?? 0) - (a.currentPeriodUsage ?? 0)
    if (usageDelta !== 0) return usageDelta

    const aName = a.displayName || a.clientAppId
    const bName = b.displayName || b.clientAppId
    return aName.localeCompare(bName)
  })
  const visibleClients = clientsByUtilization.slice(0, MAX_CLIENT_OVERVIEW_CARDS)
  const hiddenClientCount = Math.max(0, clientsByUtilization.length - visibleClients.length)

  // Sorting
  const sorted = [...logs].sort((a, b) => {
    const aVal = a[sortKey]
    const bVal = b[sortKey]
    if (typeof aVal === "number" && typeof bVal === "number") {
      return sortDir === "asc" ? aVal - bVal : bVal - aVal
    }
    const aStr = String(aVal)
    const bStr = String(bVal)
    return sortDir === "asc" ? aStr.localeCompare(bStr) : bStr.localeCompare(aStr)
  })

  const toggleSort = (key: SortKey) => {
    setUsagePage(0)
    if (sortKey === key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"))
    } else {
      setSortKey(key)
      setSortDir("desc")
    }
  }

  const usagePageCount = Math.max(1, Math.ceil(sorted.length / USAGE_PAGE_SIZE))
  const pagedSorted = sorted.slice(usagePage * USAGE_PAGE_SIZE, (usagePage + 1) * USAGE_PAGE_SIZE)

  const chartTextColor = resolvedTheme === "dark" ? "#a39e99" : "#71706e"
  const gridColor = resolvedTheme === "dark" ? "#3b3a39" : "#e1dfdd"

  if (error) {
    return (
      <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-destructive">
        Error: {error}
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Live indicator */}
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <span className="relative flex h-2.5 w-2.5">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-green-400 opacity-75" />
          <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-green-500" />
        </span>
        Live — refreshing every 10s
        {loading && <span className="text-xs">(updating…)</span>}
      </div>

      {/* KPI Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-5">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Total Cost (Ours)</CardTitle>
            <DollarSign className="h-4 w-4 text-[#107C10]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">${totalCostToUs.toFixed(2)}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Overbilled Revenue</CardTitle>
            <TrendingUp className="h-4 w-4 text-[#00B7C3]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">${totalRevenue.toFixed(2)}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Total Tokens</CardTitle>
            <Coins className="h-4 w-4 text-[#0078D4]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">{totalTokens.toLocaleString()}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Unique Clients</CardTitle>
            <Users className="h-4 w-4 text-[#0078D4]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">{uniqueClients}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Active Models</CardTitle>
            <Cpu className="h-4 w-4 text-[#FFB900]" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold font-mono">{activeModels}</div>
          </CardContent>
        </Card>
      </div>

      {/* Client Overview */}
      {clients.length > 0 && (
        <div>
          <div className="mb-3 flex items-center justify-between gap-3">
            <h2 className="text-lg font-semibold inline-flex items-center gap-2">
              <Users className="h-5 w-5 text-[#0078D4]" /> Top Clients by Utilization
            </h2>
            <span className="text-xs text-muted-foreground">
              Showing top {visibleClients.length} by current period usage
            </span>
          </div>
          {hiddenClientCount > 0 && (
            <p className="mb-3 text-xs text-muted-foreground">
              {hiddenClientCount} additional client{hiddenClientCount === 1 ? "" : "s"} not shown.
            </p>
          )}
          <div className="grid gap-4 md:grid-cols-2">
            {visibleClients.map((client) => {
              const plan = planMap.get(client.planId)
              const quota = plan?.monthlyTokenQuota ?? 0
              const usage = client.currentPeriodUsage
              const pct = quota > 0 ? (usage / quota) * 100 : 0
              const clientCosts = costByClient.get(`${client.clientAppId}:${client.tenantId}`)

              return (
                <Card key={`${client.clientAppId}:${client.tenantId}`} className="relative overflow-hidden">
                  <CardHeader className="pb-3">
                    <div className="flex items-center justify-between">
                      <button
                        className="text-left cursor-pointer"
                        onClick={() => onSelectClient?.(client.clientAppId, client.tenantId)}
                      >
                        <CardTitle className="text-lg hover:text-[#0078D4] transition-colors">
                          {client.displayName || client.clientAppId}
                        </CardTitle>
                        <p className="text-xs text-muted-foreground font-mono mt-0.5">{client.clientAppId}</p>
                        {client.tenantId && (
                          <p className="text-[10px] text-muted-foreground/70 font-mono">tenant: {client.tenantId}</p>
                        )}
                      </button>
                      {plan && <Badge variant="blue">{plan.name}</Badge>}
                    </div>
                  </CardHeader>
                  <CardContent className="space-y-4">
                    {/* Quota Usage Meter */}
                    <div>
                      <div className="flex justify-between text-sm mb-1">
                        <span className="font-medium">Quota Usage</span>
                        <span className="font-mono text-xs text-muted-foreground">
                          {usage.toLocaleString()} / {quota.toLocaleString()} tokens ({pct.toFixed(2)}%)
                        </span>
                      </div>
                      <Progress value={usage} max={quota || 1} />
                    </div>

                    {/* Per-deployment usage when not rolling up */}
                    {plan && !plan.rollUpAllDeployments && Object.keys(client.deploymentUsage ?? {}).length > 0 && (
                      <div className="space-y-2">
                        <span className="text-xs font-medium text-muted-foreground">Per-Deployment Usage</span>
                        {Object.entries(client.deploymentUsage).map(([dep, depUsage]) => {
                          const depQuota = plan.deploymentQuotas?.[dep] ?? quota
                          const depPct = depQuota > 0 ? (depUsage / depQuota) * 100 : 0
                          return (
                            <div key={dep}>
                              <div className="flex justify-between text-xs mb-0.5">
                                <span className="font-mono">{dep}</span>
                                <span className="text-muted-foreground">
                                  {depUsage.toLocaleString()} / {depQuota.toLocaleString()} ({depPct.toFixed(1)}%)
                                </span>
                              </div>
                              <Progress value={depUsage} max={depQuota || 1} className="h-1.5" />
                            </div>
                          )
                        })}
                      </div>
                    )}

                    {/* Rate Limits */}
                    {plan && (
                      <div className="flex gap-6 text-xs">
                        <div className="flex-1">
                          <div className="flex justify-between mb-0.5">
                            <span className="text-muted-foreground">RPM</span>
                            <span className="font-mono">{(client.currentRpm ?? 0).toLocaleString()} / {plan.requestsPerMinuteLimit.toLocaleString()}</span>
                          </div>
                          <Progress value={client.currentRpm ?? 0} max={plan.requestsPerMinuteLimit || 1} className="h-1.5" />
                        </div>
                        <div className="flex-1">
                          <div className="flex justify-between mb-0.5">
                            <span className="text-muted-foreground">TPM</span>
                            <span className="font-mono">{(client.currentTpm ?? 0).toLocaleString()} / {plan.tokensPerMinuteLimit.toLocaleString()}</span>
                          </div>
                          <Progress value={client.currentTpm ?? 0} max={plan.tokensPerMinuteLimit || 1} className="h-1.5" />
                        </div>
                      </div>
                    )}

                    {/* Cost Summary & Overbilled */}
                    <div className="flex items-center justify-between text-sm pt-1 border-t">
                      <span>
                        Our cost: <span className="font-mono font-semibold">${(clientCosts?.costToUs ?? 0).toFixed(2)}</span>
                        {" | "}
                        Customer: <span className="font-mono font-semibold">${(clientCosts?.costToCustomer ?? 0).toFixed(2)}</span>
                      </span>
                      {client.overbilledTokens > 0 && (
                        <Badge variant="red">{client.overbilledTokens.toLocaleString()} overbilled</Badge>
                      )}
                    </div>
                  </CardContent>
                </Card>
              )
            })}
          </div>
        </div>
      )}

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <CardTitle className="text-base">Utilization Trend (Last 5 Minutes)</CardTitle>
          <span className="text-xs text-muted-foreground">All clients, 10-second buckets</span>
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
                contentStyle={{ backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff", border: "1px solid #3b3a39", borderRadius: 8 }}
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

      {/* Charts */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
        {/* Bar: Cost by Deployment */}
        <Card className="lg:col-span-1">
          <CardHeader>
            <CardTitle className="text-base">Cost by Deployment</CardTitle>
          </CardHeader>
          <CardContent className="h-[280px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={costByDeployment} layout="vertical" margin={{ left: 20 }}>
                <CartesianGrid strokeDasharray="3 3" stroke={gridColor} />
                <XAxis type="number" tick={{ fill: chartTextColor, fontSize: 12 }} tickFormatter={(v: number) => `$${v}`} />
                <YAxis dataKey="deploymentId" type="category" tick={{ fill: chartTextColor, fontSize: 11 }} width={130} />
                <Tooltip
                  contentStyle={{ backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff", border: "1px solid #3b3a39", borderRadius: 8 }}
                  formatter={(value) => [`$${(Number(value) || 0).toFixed(4)}`, "Cost"]}
                />
                <Bar dataKey="cost" radius={[0, 4, 4, 0]}>
                  {costByDeployment.map((entry, i) => (
                    <Cell key={i} fill={getDeploymentStyle(entry.deploymentId).hex} />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>

        {/* Pie: Tokens by Client */}
        <Card className="lg:col-span-1">
          <CardHeader>
            <CardTitle className="text-base">Tokens by Client</CardTitle>
          </CardHeader>
          <CardContent className="h-[280px]">
            <ResponsiveContainer width="100%" height="100%">
              <PieChart>
                <Pie
                  data={tokensByClient}
                  dataKey="tokens"
                  nameKey="name"
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={90}
                  paddingAngle={2}
                  label={({ name }) => name}
                >
                  {tokensByClient.map((_entry, i) => (
                    <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Tooltip
                  contentStyle={{ backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff", border: "1px solid #3b3a39", borderRadius: 8 }}
                  formatter={(value) => [(Number(value) || 0).toLocaleString(), "Tokens"]}
                />
              </PieChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>

        {/* Stacked Bar: Prompt vs Completion by deployment */}
        <Card className="lg:col-span-1">
          <CardHeader>
            <CardTitle className="text-base">Prompt vs Completion Tokens by Deployment</CardTitle>
          </CardHeader>
          <CardContent className="h-[280px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={usageTrend}>
                <CartesianGrid strokeDasharray="3 3" stroke={gridColor} />
                <XAxis dataKey="deploymentId" tick={{ fill: chartTextColor, fontSize: 11 }} />
                <YAxis tick={{ fill: chartTextColor, fontSize: 12 }} />
                <Tooltip
                  contentStyle={{ backgroundColor: resolvedTheme === "dark" ? "#201F1E" : "#fff", border: "1px solid #3b3a39", borderRadius: 8 }}
                  formatter={(value, name) => [(Number(value) || 0).toLocaleString(), name === "prompt" ? "Prompt" : "Completion"]}
                />
                <Legend />
                <Bar dataKey="prompt" stackId="1" fill="#0078D4" name="Prompt" />
                <Bar dataKey="completion" stackId="1" fill="#00B7C3" name="Completion" />
              </BarChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>
      </div>

      {/* Data Table */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Usage Summary</CardTitle>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                {(
                  [
                    ["clientAppId", "Client App"],
                    ["tenantId", "Tenant"],
                    ["deploymentId", "Deployment"],
                    ["model", "Model"],
                    ["promptTokens", "Prompt Tokens"],
                    ["completionTokens", "Completion Tokens"],
                    ["totalTokens", "Total Tokens"],
                    ["totalCost", "Total Cost"],
                    ["costToUs", "Our Cost"],
                    ["costToCustomer", "Customer Cost"],
                    ["isOverbilled", "Overbilled"],
                  ] as [SortKey, string][]
                ).map(([key, label]) => (
                  <TableHead
                    key={key}
                    className="cursor-pointer select-none hover:text-foreground transition-colors"
                    onClick={() => toggleSort(key)}
                  >
                    <span className="inline-flex items-center gap-1">
                      {label}
                      <ArrowUpDown className="h-3 w-3 opacity-50" />
                    </span>
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {pagedSorted.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={11} className="text-center text-muted-foreground py-8">
                    No usage entries found.
                  </TableCell>
                </TableRow>
              ) : (
                pagedSorted.map((entry, idx) => {
                  const deploymentStyle = getDeploymentStyle(entry.deploymentId)
                  return (
                    <TableRow key={`${entry.clientAppId}-${entry.deploymentId}-${idx}`}>
                      <TableCell>
                        <button
                          className="text-left text-[#0078D4] hover:underline cursor-pointer"
                          onClick={() => onSelectClient?.(entry.clientAppId, entry.tenantId)}
                        >
                          <span className="block text-sm font-medium">{clientNameMap.get(`${entry.clientAppId}:${entry.tenantId}`) ?? entry.clientAppId}</span>
                          {clientNameMap.has(`${entry.clientAppId}:${entry.tenantId}`) && (
                            <span className="block font-mono text-[10px] text-muted-foreground">{entry.clientAppId}</span>
                          )}
                        </button>
                      </TableCell>
                      <TableCell className="font-mono text-xs text-muted-foreground">{entry.tenantId}</TableCell>
                      <TableCell>
                        <Badge variant={deploymentStyle.variant}>{entry.deploymentId || "unknown"}</Badge>
                      </TableCell>
                      <TableCell className="font-mono text-xs text-muted-foreground">{entry.model ?? "unknown"}</TableCell>
                      <TableCell className="font-mono text-right">{entry.promptTokens.toLocaleString()}</TableCell>
                      <TableCell className="font-mono text-right">{entry.completionTokens.toLocaleString()}</TableCell>
                      <TableCell className="font-mono text-right">{entry.totalTokens.toLocaleString()}</TableCell>
                      <TableCell className="font-mono text-right font-semibold">{entry.totalCost}</TableCell>
                      <TableCell className="font-mono text-right">{entry.costToUs ?? "—"}</TableCell>
                      <TableCell className="font-mono text-right">{entry.costToCustomer ?? "—"}</TableCell>
                      <TableCell>
                        {entry.isOverbilled ? (
                          <Badge variant="red">Overbilled</Badge>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  )
                })
              )}
            </TableBody>
          </Table>
          {sorted.length > USAGE_PAGE_SIZE && (
            <div className="flex items-center justify-between pt-4 border-t mt-4">
              <span className="text-sm text-muted-foreground">
                Showing {usagePage * USAGE_PAGE_SIZE + 1}–{Math.min((usagePage + 1) * USAGE_PAGE_SIZE, sorted.length)} of {sorted.length} entries
              </span>
              <div className="flex gap-2">
                <Button variant="outline" size="sm" disabled={usagePage === 0} onClick={() => setUsagePage((p) => p - 1)}>
                  Previous
                </Button>
                <Button variant="outline" size="sm" disabled={usagePage >= usagePageCount - 1} onClick={() => setUsagePage((p) => p + 1)}>
                  Next
                </Button>
              </div>
            </div>
          )}
        </CardContent>
      </Card>
      {/* Request Log */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <CardTitle className="text-base inline-flex items-center gap-2">
            <ScrollText className="h-4 w-4" /> Request Log
          </CardTitle>
          <span className="text-xs text-muted-foreground">Last 20 requests — auto-refreshing</span>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Timestamp</TableHead>
                <TableHead>Client</TableHead>
                <TableHead>Model</TableHead>
                <TableHead className="text-right">Tokens</TableHead>
                <TableHead className="text-right">Our Cost</TableHead>
                <TableHead className="text-right">Customer Cost</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {recentRequestLogs.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={7} className="text-center text-muted-foreground py-8">
                    No request log entries yet.
                  </TableCell>
                </TableRow>
              ) : (
                recentRequestLogs.map((req, idx) => (
                  <TableRow key={`${req.timestamp}-${req.clientAppId}-${idx}`}>
                    <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                      {(() => { try { return new Date(req.timestamp).toLocaleString() } catch { return req.timestamp } })()}
                    </TableCell>
                    <TableCell>
                      <button
                        className="text-left text-[#0078D4] hover:underline cursor-pointer"
                        onClick={() => onSelectClient?.(req.clientAppId, req.tenantId)}
                      >
                        <span className="block text-sm">{req.clientDisplayName || clientNameMap.get(`${req.clientAppId}:${req.tenantId}`) || req.clientAppId}</span>
                      </button>
                    </TableCell>
                    <TableCell>
                      <Badge variant={getModelStyle(req.model).variant}>{req.model ?? "unknown"}</Badge>
                    </TableCell>
                    <TableCell className="font-mono text-right">{req.totalTokens.toLocaleString()}</TableCell>
                    <TableCell className="font-mono text-right">{req.costToUs}</TableCell>
                    <TableCell className="font-mono text-right">{req.costToCustomer}</TableCell>
                    <TableCell>
                      {req.statusCode === 429 ? (
                        <Badge variant="red">429</Badge>
                      ) : req.isOverbilled ? (
                        <Badge variant="amber">Overbilled</Badge>
                      ) : req.statusCode >= 200 && req.statusCode < 300 ? (
                        <Badge variant="green">OK</Badge>
                      ) : (
                        <Badge variant="secondary">{req.statusCode}</Badge>
                      )}
                    </TableCell>
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

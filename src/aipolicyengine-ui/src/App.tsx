import { useState, useEffect, useMemo, useCallback } from "react"
import { useIsAuthenticated, useMsal } from "@azure/msal-react"
import { InteractionStatus } from "@azure/msal-browser"
import { Layout } from "./components/Layout"
import { Dashboard } from "./pages/Dashboard"
import { Clients } from "./pages/Quotas"
import { Plans } from "./pages/Plans"
import { Pricing } from "./pages/Pricing"
import { Export } from "./pages/Export"
import { ClientDetail } from "./pages/ClientDetail"
import { RoutingPolicies } from "./pages/RoutingPolicies"
import { RequestBilling } from "./pages/RequestBilling"
import { AccessProfiles } from "./pages/AccessProfiles"
import { Apis } from "./pages/Apis"
import { loginRequest } from "./auth/msalConfig"
import { fetchPlans } from "./api"
import type { PlanData, BillingMode } from "./types"
import { Button } from "./components/ui/button"
import { Activity, LogIn } from "lucide-react"

const TAB_PATHS = {
  dashboard: "/",
  clients: "/clients",
  plans: "/plans",
  pricing: "/pricing",
  routing: "/routing",
  access: "/access",
  apis: "/apis",
  requests: "/request-billing",
  export: "/export",
} as const

type TabId = keyof typeof TAB_PATHS

function resolveTabFromPathname(pathname: string): TabId {
  const normalizedPath = pathname === "/" ? "/" : pathname.replace(/\/$/, "")
  const matchingEntry = Object.entries(TAB_PATHS).find(([, path]) => path === normalizedPath)
  return (matchingEntry?.[0] as TabId | undefined) ?? "dashboard"
}

function App() {
  const [activeTab, setActiveTab] = useState<TabId>(() => resolveTabFromPathname(window.location.pathname))
  const [selectedClient, setSelectedClient] = useState<{ clientAppId: string; tenantId: string } | null>(null)
  const [plans, setPlans] = useState<PlanData[]>([])
  const isAuthenticated = useIsAuthenticated()
  const { instance, inProgress } = useMsal()

  const handleTabChange = useCallback((tab: string) => {
    const nextTab = (tab in TAB_PATHS ? tab : "dashboard") as TabId
    setActiveTab(nextTab)

    const nextPath = TAB_PATHS[nextTab]
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath)
    }
  }, [])

  useEffect(() => {
    const handlePopState = () => {
      setActiveTab(resolveTabFromPathname(window.location.pathname))
      setSelectedClient(null)
    }

    window.addEventListener("popstate", handlePopState)
    return () => window.removeEventListener("popstate", handlePopState)
  }, [])

  useEffect(() => {
    if (!isAuthenticated) return

    let cancelled = false

    void (async () => {
      try {
        const res = await fetchPlans()
        if (!cancelled) {
          setPlans(res.plans ?? [])
        }
      } catch {
        if (!cancelled) {
          setPlans([])
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [isAuthenticated])

  const billingMode: BillingMode = useMemo(() => {
    if (plans.length === 0) return "token"
    const hasMultiplier = plans.some((plan) => plan.useMultiplierBilling)
    const hasToken = plans.some((plan) => !plan.useMultiplierBilling)
    if (hasMultiplier && hasToken) return "hybrid"
    if (hasMultiplier) return "multiplier"
    return "token"
  }, [plans])

  if (inProgress !== InteractionStatus.None) {
    return (
      <div className="flex h-screen items-center justify-center bg-background">
        <div className="flex flex-col items-center gap-4">
          <Activity className="h-10 w-10 animate-pulse text-blue-500" />
          <p className="text-muted-foreground">Authenticating…</p>
        </div>
      </div>
    )
  }

  if (!isAuthenticated) {
    return (
      <div className="flex h-screen items-center justify-center bg-background">
        <div className="max-w-sm rounded-xl border bg-card p-8 text-center shadow-lg">
          <div className="flex flex-col items-center gap-6">
            <Activity className="h-12 w-12 text-blue-500" />
            <div>
              <h1 className="mb-2 text-2xl font-bold">AI Policy Engine Dashboard</h1>
              <p className="text-sm text-muted-foreground">Sign in with your organization account to access the dashboard.</p>
            </div>
            <Button onClick={() => instance.loginRedirect(loginRequest)} className="w-full gap-2">
              <LogIn className="h-4 w-4" />
              Sign in with Entra ID
            </Button>
          </div>
        </div>
      </div>
    )
  }

  if (selectedClient) {
    return (
      <Layout activeTab={activeTab} onTabChange={(tab) => { setSelectedClient(null); handleTabChange(tab) }} billingMode={billingMode}>
        <ClientDetail clientAppId={selectedClient.clientAppId} tenantId={selectedClient.tenantId} onBack={() => setSelectedClient(null)} />
      </Layout>
    )
  }

  return (
    <Layout activeTab={activeTab} onTabChange={handleTabChange} billingMode={billingMode}>
      {activeTab === "dashboard" && <Dashboard onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "clients" && <Clients onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "plans" && <Plans />}
      {activeTab === "pricing" && <Pricing />}
      {activeTab === "routing" && <RoutingPolicies />}
      {activeTab === "access" && <AccessProfiles />}
      {activeTab === "apis" && <Apis />}
      {activeTab === "requests" && <RequestBilling onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "export" && <Export />}
    </Layout>
  )
}

export default App

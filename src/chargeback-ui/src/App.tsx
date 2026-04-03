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
import { loginRequest } from "./auth/msalConfig"
import { fetchPlans } from "./api"
import type { PlanData, BillingMode } from "./types"
import { Button } from "./components/ui/button"
import { Activity, LogIn } from "lucide-react"

function App() {
  const [activeTab, setActiveTab] = useState("dashboard")
  const [selectedClient, setSelectedClient] = useState<{ clientAppId: string; tenantId: string } | null>(null)
  const [plans, setPlans] = useState<PlanData[]>([])
  const isAuthenticated = useIsAuthenticated()
  const { instance, inProgress } = useMsal()

  const loadPlans = useCallback(async () => {
    try {
      const res = await fetchPlans()
      setPlans(res.plans ?? [])
    } catch {
      // Plans may not be loaded yet — billing mode defaults to token
    }
  }, [])

  useEffect(() => {
    if (isAuthenticated) loadPlans()
  }, [isAuthenticated, loadPlans])

  // Adaptive billing mode
  const billingMode: BillingMode = useMemo(() => {
    if (plans.length === 0) return 'token'
    const hasMultiplier = plans.some(p => p.useMultiplierBilling)
    const hasToken = plans.some(p => !p.useMultiplierBilling)
    if (hasMultiplier && hasToken) return 'hybrid'
    if (hasMultiplier) return 'multiplier'
    return 'token'
  }, [plans])

  if (inProgress !== InteractionStatus.None) {
    return (
      <div className="flex h-screen items-center justify-center bg-background">
        <div className="flex flex-col items-center gap-4">
          <Activity className="h-10 w-10 text-blue-500 animate-pulse" />
          <p className="text-muted-foreground">Authenticating…</p>
        </div>
      </div>
    )
  }

  if (!isAuthenticated) {
    return (
      <div className="flex h-screen items-center justify-center bg-background">
        <div className="flex flex-col items-center gap-6 p-8 rounded-xl border bg-card shadow-lg max-w-sm text-center">
          <Activity className="h-12 w-12 text-blue-500" />
          <div>
            <h1 className="text-2xl font-bold mb-2">Chargeback Dashboard</h1>
            <p className="text-muted-foreground text-sm">Sign in with your organization account to access the dashboard.</p>
          </div>
          <Button onClick={() => instance.loginRedirect(loginRequest)} className="gap-2 w-full">
            <LogIn className="h-4 w-4" />
            Sign in with Entra ID
          </Button>
        </div>
      </div>
    )
  }

  if (selectedClient) {
    return (
      <Layout activeTab={activeTab} onTabChange={(tab) => { setSelectedClient(null); setActiveTab(tab); }} billingMode={billingMode}>
        <ClientDetail clientAppId={selectedClient.clientAppId} tenantId={selectedClient.tenantId} onBack={() => setSelectedClient(null)} />
      </Layout>
    )
  }

  return (
    <Layout activeTab={activeTab} onTabChange={setActiveTab} billingMode={billingMode}>
      {activeTab === "dashboard" && <Dashboard onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "clients" && <Clients onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "plans" && <Plans />}
      {activeTab === "pricing" && <Pricing />}
      {activeTab === "routing" && <RoutingPolicies />}
      {activeTab === "requests" && <RequestBilling onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "export" && <Export />}
    </Layout>
  )
}

export default App

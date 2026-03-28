import { useState } from "react"
import { useIsAuthenticated, useMsal } from "@azure/msal-react"
import { InteractionStatus } from "@azure/msal-browser"
import { Layout } from "./components/Layout"
import { Dashboard } from "./pages/Dashboard"
import { Clients } from "./pages/Quotas"
import { Plans } from "./pages/Plans"
import { Pricing } from "./pages/Pricing"
import { Export } from "./pages/Export"
import { ClientDetail } from "./pages/ClientDetail"
import { loginRequest } from "./auth/msalConfig"
import { Button } from "./components/ui/button"
import { Activity, LogIn } from "lucide-react"

function App() {
  const [activeTab, setActiveTab] = useState("dashboard")
  const [selectedClient, setSelectedClient] = useState<{ clientAppId: string; tenantId: string } | null>(null)
  const isAuthenticated = useIsAuthenticated()
  const { instance, inProgress } = useMsal()

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
      <Layout activeTab={activeTab} onTabChange={(tab) => { setSelectedClient(null); setActiveTab(tab); }}>
        <ClientDetail clientAppId={selectedClient.clientAppId} tenantId={selectedClient.tenantId} onBack={() => setSelectedClient(null)} />
      </Layout>
    )
  }

  return (
    <Layout activeTab={activeTab} onTabChange={setActiveTab}>
      {activeTab === "dashboard" && <Dashboard onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "clients" && <Clients onSelectClient={(clientAppId, tenantId) => setSelectedClient({ clientAppId, tenantId })} />}
      {activeTab === "plans" && <Plans />}
      {activeTab === "pricing" && <Pricing />}
      {activeTab === "export" && <Export />}
    </Layout>
  )
}

export default App

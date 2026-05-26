import { useMemo, useState } from "react"
import { Search, UserRoundCog } from "lucide-react"
import { Badge } from "../ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "../ui/card"
import { Input } from "../ui/input"
import type { ClientAssignment, PlanData } from "../../types"
import { cn } from "../../lib/utils"

interface ClientListProps {
  clients: ClientAssignment[]
  plans: PlanData[]
  selectedClientKey: string
  onSelectClient: (clientKey: string) => void
}

function buildClientKey(client: Pick<ClientAssignment, "clientAppId" | "tenantId">): string {
  return `${client.clientAppId}|${client.tenantId}`
}

export function ClientList({ clients, plans, selectedClientKey, onSelectClient }: ClientListProps) {
  const [query, setQuery] = useState("")

  const plansById = useMemo(
    () => Object.fromEntries(plans.map((plan) => [plan.id, plan])),
    [plans],
  )

  const filteredClients = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase()
    if (!normalizedQuery) return clients

    return clients.filter((client) => {
      const haystacks = [
        client.displayName,
        client.clientAppId,
        client.tenantId,
        plansById[client.planId]?.name,
      ]

      return haystacks.some((value) => value?.toLowerCase().includes(normalizedQuery))
    })
  }, [clients, plansById, query])

  return (
    <Card className="h-full overflow-hidden border-slate-300/70 bg-gradient-to-b from-slate-50 via-white to-slate-100/80 shadow-sm dark:border-slate-800 dark:from-slate-950 dark:via-slate-950 dark:to-slate-900">
      <CardHeader className="border-b border-slate-200/70 pb-4 dark:border-slate-800">
        <CardTitle className="flex items-center gap-2 text-base">
          <UserRoundCog className="h-4 w-4 text-[#0078D4]" />
          Clients
          <Badge variant="secondary" className="ml-auto flex-shrink-0">{clients.length}</Badge>
        </CardTitle>
        <div className="relative mt-3">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search client, tenant, plan…" className="pl-9" />
        </div>
      </CardHeader>
      <CardContent className="p-0">
        <div className="max-h-[72vh] overflow-auto">
          {filteredClients.length === 0 ? (
            <div className="p-6 text-center text-sm text-muted-foreground">No clients match your search.</div>
          ) : (
            <ul className="divide-y divide-slate-200/70 dark:divide-slate-800">
              {filteredClients.map((client) => {
                const clientKey = buildClientKey(client)
                const selected = clientKey === selectedClientKey
                const plan = plansById[client.planId]

                return (
                  <li key={clientKey}>
                    <button
                      type="button"
                      onClick={() => onSelectClient(clientKey)}
                      className={cn(
                        "flex w-full min-w-0 flex-col gap-2 px-4 py-3 text-left transition-colors",
                        selected
                          ? "bg-primary/10 text-foreground"
                          : "hover:bg-accent/70",
                      )}
                    >
                      <div className="flex w-full items-center gap-2">
                        <span className="min-w-0 flex-1 truncate font-medium">{client.displayName || client.clientAppId}</span>
                        <Badge className="flex-shrink-0" variant={selected ? "blue" : "outline"}>
                          {plan?.name ?? client.planId}
                        </Badge>
                      </div>
                      <div className="grid gap-1 text-xs text-muted-foreground">
                        <span className="truncate font-mono">{client.clientAppId}</span>
                        <span className="truncate font-mono">{client.tenantId}</span>
                      </div>
                    </button>
                  </li>
                )
              })}
            </ul>
          )}
        </div>
      </CardContent>
    </Card>
  )
}

import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "../ui/card"
import { ChevronDown, ChevronRight, Globe, RefreshCcw, Workflow } from "lucide-react"
import type { ApimApiSummary, ApimOperationSummary } from "../../types/apim"

interface ApiTreeProps {
  apis: ApimApiSummary[]
  expandedApiIds: string[]
  loadingOperationApiIds: string[]
  operationsByApi: Record<string, ApimOperationSummary[]>
  operationErrors: Record<string, string | null>
  selectedKey?: string
  onApiToggle: (api: ApimApiSummary) => void
  onApiSelect: (api: ApimApiSummary) => void
  onOperationSelect: (api: ApimApiSummary, operation: ApimOperationSummary) => void
  onRetryOperations: (api: ApimApiSummary) => void
}

function isExpanded(expandedApiIds: string[], apiId: string): boolean {
  return expandedApiIds.includes(apiId)
}

function isLoading(loadingOperationApiIds: string[], apiId: string): boolean {
  return loadingOperationApiIds.includes(apiId)
}

export function ApiTree({
  apis,
  expandedApiIds,
  loadingOperationApiIds,
  operationsByApi,
  operationErrors,
  selectedKey,
  onApiToggle,
  onApiSelect,
  onOperationSelect,
  onRetryOperations,
}: ApiTreeProps) {
  return (
    <Card className="h-full">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Globe className="h-4 w-4 text-[#0078D4]" />
          APIs
          <Badge variant="secondary" className="ml-auto">{apis.length}</Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2">
        {apis.length === 0 ? (
          <div className="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
            No APIs available from APIM yet.
          </div>
        ) : (
          <ul role="tree" aria-label="APIs and operations" className="space-y-1">
            {apis.map((api) => {
              const expanded = isExpanded(expandedApiIds, api.id)
              const operations = operationsByApi[api.id] ?? []
              const loading = isLoading(loadingOperationApiIds, api.id)
              const operationError = operationErrors[api.id]
              const apiKey = `api:${api.id}`

              return (
                <li key={api.id} role="treeitem" aria-expanded={expanded}>
                  <div className="flex items-start gap-2 rounded-lg border border-transparent p-2 hover:bg-muted/50">
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 shrink-0"
                      onClick={() => onApiToggle(api)}
                      aria-label={`${expanded ? "Collapse" : "Expand"} ${api.displayName}`}
                    >
                      {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                    </Button>
                    <button
                      type="button"
                      onClick={() => onApiSelect(api)}
                      className={`flex min-w-0 flex-1 flex-col items-start rounded-md px-2 py-1 text-left transition-colors ${
                        selectedKey === apiKey ? "bg-accent text-accent-foreground" : "hover:bg-accent/70"
                      }`}
                      aria-current={selectedKey === apiKey ? "page" : undefined}
                    >
                      <div className="flex w-full items-center gap-2">
                        <span className="truncate font-medium">{api.displayName}</span>
                        {api.isCurrent && <Badge variant="green">Current</Badge>}
                      </div>
                      <span className="truncate font-mono text-xs text-muted-foreground">/{api.path}</span>
                      {api.serviceUrl && (
                        <span className="truncate text-xs text-muted-foreground">{api.serviceUrl}</span>
                      )}
                    </button>
                  </div>

                  {expanded && (
                    <div className="ml-10 mt-1 space-y-1 border-l pl-3">
                      {loading && <div className="py-2 text-sm text-muted-foreground">Loading operations…</div>}

                      {!loading && operationError && (
                        <div className="flex items-center gap-2 rounded-md border border-destructive/40 bg-destructive/5 p-2 text-xs text-destructive">
                          <span className="min-w-0 flex-1">{operationError}</span>
                          <Button type="button" variant="ghost" size="sm" onClick={() => onRetryOperations(api)}>
                            <RefreshCcw className="h-3.5 w-3.5" />
                            Retry
                          </Button>
                        </div>
                      )}

                      {!loading && !operationError && operations.length === 0 && (
                        <div className="py-2 text-sm text-muted-foreground">No operations found.</div>
                      )}

                      {!loading && !operationError && operations.map((operation) => {
                        const operationKey = `operation:${api.id}:${operation.id}`
                        return (
                          <button
                            key={operation.id}
                            type="button"
                            onClick={() => onOperationSelect(api, operation)}
                            className={`flex w-full items-start gap-2 rounded-md px-2 py-2 text-left text-sm transition-colors ${
                              selectedKey === operationKey ? "bg-accent text-accent-foreground" : "hover:bg-accent/70"
                            }`}
                            aria-current={selectedKey === operationKey ? "page" : undefined}
                          >
                            <Workflow className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                            <div className="min-w-0">
                              <div className="flex items-center gap-2">
                                <span className="truncate font-medium">{operation.displayName}</span>
                                <Badge variant="outline">{operation.method}</Badge>
                              </div>
                              <div className="truncate font-mono text-xs text-muted-foreground">{operation.urlTemplate}</div>
                            </div>
                          </button>
                        )
                      })}
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

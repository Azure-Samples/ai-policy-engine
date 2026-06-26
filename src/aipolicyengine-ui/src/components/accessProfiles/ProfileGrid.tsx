import { ChevronDown, ChevronRight, Layers2, ListFilter, RefreshCcw, Search, Shield, Sparkles, X } from "lucide-react"
import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "../ui/card"
import { Input } from "../ui/input"
import { CascadeBadge } from "./CascadeBadge"
import { OVERRIDE_FILTERS, type OverrideFilter, type FilteredSection } from "./filtering"
import { cn } from "../../lib/utils"
import type { ClientAssignment, ModelRoutingPolicy, PlanData } from "../../types"
import type { AccessProfile } from "../../types/accessProfiles"
import type { ApimApiSummary } from "../../types/apim"
import type { AccessGridCellData, AccessScopeTarget } from "./types"

interface ProfileGridProps {
  client: ClientAssignment | null
  globalCell: AccessGridCellData | null
  sections: Array<{
    api: ApimApiSummary
    apiCell: AccessGridCellData
    operationCells: AccessGridCellData[]
    directOverrideCount: number
    expanded: boolean
    loadingOperations: boolean
    operationError: string | null
  }>
  plansById: Record<string, PlanData>
  routingPoliciesById: Record<string, ModelRoutingPolicy>
  queuedScopeKeys: string[]
  profilesLoading: boolean
  onToggleApi: (api: ApimApiSummary) => void
  onRetryOperations: (api: ApimApiSummary) => void
  onOpenCell: (target: AccessScopeTarget, directProfile: AccessProfile | null, effective: AccessGridCellData["effective"]) => void
  onToggleQueuedScope: (target: AccessScopeTarget) => void
  // Filter state from parent
  filteredSections: FilteredSection[]
  globalVisible: boolean
  visibleScopeCount: number
  filtersActive: boolean
  searchQuery: string
  overrideFilter: OverrideFilter
  onSearchChange: (query: string) => void
  onOverrideFilterChange: (filter: OverrideFilter) => void
  onClearFilters: () => void
}

function scopeKey(target: AccessScopeTarget): string {
  return `${target.apiId}:${target.operationId ?? "_all"}`
}

function deploymentLabel(deployments: string[]): string[] {
  return deployments.length > 0 ? deployments : ["All deployments"]
}

function sourceVariant(source: "direct" | "api" | "global" | "client"): "teal" | "cyan" | "amber" | "green" {
  switch (source) {
    case "api":
      return "teal"
    case "global":
      return "cyan"
    case "client":
      return "amber"
    default:
      return "green"
  }
}

function directTone(profile: AccessProfile | null): string {
  if (!profile) return "border-dashed border-slate-300/70 bg-slate-500/5 dark:border-slate-700/70 dark:bg-slate-500/10"
  if (!profile.enabled) return "border-amber-400/50 bg-amber-500/10"
  return "border-emerald-500/30 bg-emerald-500/10"
}

function renderSummary(
  cell: AccessGridCellData,
  plansById: Record<string, PlanData>,
  routingPoliciesById: Record<string, ModelRoutingPolicy>,
) {
  const effective = cell.effective
  if (!effective) {
    return <p className="text-sm text-muted-foreground">No direct or inherited profile resolves at this scope.</p>
  }

  const planName = plansById[effective.planId]?.name ?? effective.planId
  const routingName = effective.routingPolicyId
    ? (routingPoliciesById[effective.routingPolicyId]?.name ?? effective.routingPolicyId)
    : "No routing override"
  const deployments = deploymentLabel(effective.allowedDeployments)

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="blue">{planName}</Badge>
        <Badge variant={sourceVariant(effective.source)}>
          {effective.source === "direct" ? (cell.directProfile?.enabled ? "Direct override" : "Direct override · disabled") : effective.sourceLabel}
        </Badge>
        {effective.routingPolicyId ? (
          <Badge variant="teal">{routingName}</Badge>
        ) : (
          <Badge variant="outline">{routingName}</Badge>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        {deployments.map((deployment) => (
          <span key={deployment} className="rounded-full border px-2.5 py-1 text-xs text-muted-foreground">
            {deployment}
          </span>
        ))}
      </div>
    </div>
  )
}

function ScopeRow({
  cell,
  plansById,
  routingPoliciesById,
  queued,
  onOpenCell,
  onToggleQueuedScope,
}: {
  cell: AccessGridCellData
  plansById: Record<string, PlanData>
  routingPoliciesById: Record<string, ModelRoutingPolicy>
  queued: boolean
  onOpenCell: (target: AccessScopeTarget, directProfile: AccessProfile | null, effective: AccessGridCellData["effective"]) => void
  onToggleQueuedScope: (target: AccessScopeTarget) => void
}) {
  const { target, directProfile, effective } = cell
  const summary = renderSummary(cell, plansById, routingPoliciesById)

  return (
    <div className="grid gap-3 border-t border-slate-200/70 px-4 py-4 first:border-t-0 md:grid-cols-[260px_minmax(0,1fr)] dark:border-slate-800">
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <p className="font-medium text-foreground">
            {target.kind === "global" ? "Client-global" : target.operationDisplayName ?? "API-wide default"}
          </p>
          {target.kind === "operation" && target.method && (
            <Badge variant="outline">{target.method}</Badge>
          )}
        </div>
        <p className="text-xs font-mono text-muted-foreground">
          {target.kind === "global" ? "_global / _all" : `${target.apiId} / ${target.operationId ?? "_all"}`}
        </p>
        {target.kind === "operation" && target.urlTemplate && (
          <p className="truncate text-xs text-muted-foreground">{target.urlTemplate}</p>
        )}
      </div>

      <div className={cn("rounded-2xl border p-4", directTone(directProfile))}>
        <button
          type="button"
          className="block w-full text-left"
          onClick={() => onOpenCell(target, directProfile, effective)}
        >
          {summary}
        </button>

        {!directProfile && (
          <div className="mt-4">
            <CascadeBadge
              effective={effective}
              bulkQueued={queued}
              onOverride={() => onOpenCell(target, directProfile, effective)}
              onQueueBulk={() => onToggleQueuedScope(target)}
            />
          </div>
        )}
      </div>
    </div>
  )
}

export function ProfileGrid({
  client,
  globalCell,
  sections,
  plansById,
  routingPoliciesById,
  queuedScopeKeys,
  profilesLoading,
  onToggleApi,
  onRetryOperations,
  onOpenCell,
  onToggleQueuedScope,
  filteredSections,
  globalVisible,
  visibleScopeCount,
  filtersActive,
  searchQuery,
  overrideFilter,
  onSearchChange,
  onOverrideFilterChange,
  onClearFilters,
}: ProfileGridProps) {

  if (!client) {
    return (
      <Card className="h-full">
        <CardContent className="flex min-h-[520px] flex-col items-center justify-center gap-3 text-center text-muted-foreground">
          <Shield className="h-10 w-10 text-[#0078D4]" />
          <div>
            <p className="font-medium text-foreground">Select a client</p>
            <p className="text-sm">Choose a client from the left pane to inspect endpoint-scoped Access Profiles.</p>
          </div>
        </CardContent>
      </Card>
    )
  }

  return (
    <div className="space-y-5">
      <Card className="overflow-hidden border-slate-300/70 bg-gradient-to-br from-slate-950 via-slate-900 to-slate-950 text-slate-50 shadow-lg dark:border-slate-800">
        <CardHeader className="border-b border-slate-800/80">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
            <div>
              <CardTitle className="text-2xl text-white">{client.displayName || client.clientAppId}</CardTitle>
              <p className="mt-2 text-sm text-slate-300">
                Client-first access control matrix with API-wide, operation-level, and client-global inheritance.
              </p>
            </div>
            <div className="flex flex-wrap gap-2 text-xs text-slate-300">
              <Badge variant="outline" className="border-slate-600 text-slate-100">{client.planId}</Badge>
              <Badge variant="outline" className="border-slate-600 text-slate-100">{client.tenantId}</Badge>
              <Badge variant="outline" className="border-slate-600 text-slate-100">{client.clientAppId}</Badge>
            </div>
          </div>
        </CardHeader>
        <CardContent className="p-4 text-sm text-slate-300">
          Empty scopes show their current cascade result. Direct overrides stay green, disabled ones glow amber, and queued bulk overrides are highlighted in the inherited badge.
        </CardContent>
      </Card>

      <Card className="sticky top-16 z-10 overflow-hidden border-slate-300/70 shadow-sm dark:border-slate-800">
        <CardContent className="flex flex-col gap-3 p-3 lg:flex-row lg:items-center">
          <div className="relative min-w-0 flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={searchQuery}
              onChange={(event) => onSearchChange(event.target.value)}
              placeholder="Search by API, operation, plan, method, or path…"
              className="pl-9"
              aria-label="Search access profiles"
            />
          </div>
          <div
            role="group"
            aria-label="Override filter"
            className="flex items-center gap-1 rounded-lg border border-slate-200/70 p-1 dark:border-slate-800"
          >
            <ListFilter className="ml-1 h-4 w-4 flex-shrink-0 text-muted-foreground" />
            {OVERRIDE_FILTERS.map((option) => (
              <button
                key={option.value}
                type="button"
                onClick={() => onOverrideFilterChange(option.value)}
                aria-pressed={overrideFilter === option.value}
                className={cn(
                  "whitespace-nowrap rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
                  overrideFilter === option.value
                    ? "bg-primary text-primary-foreground"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
                )}
              >
                {option.label}
              </button>
            ))}
          </div>
          {filtersActive && (
            <Button type="button" variant="ghost" size="sm" className="flex-shrink-0" onClick={onClearFilters}>
              <X className="h-3.5 w-3.5" />
              Clear
            </Button>
          )}
        </CardContent>
      </Card>

      {filtersActive && (
        <p className="px-1 text-xs text-muted-foreground">
          Showing {visibleScopeCount} matching scope{visibleScopeCount === 1 ? "" : "s"}
          {overrideFilter !== "all" ? ` · ${OVERRIDE_FILTERS.find((option) => option.value === overrideFilter)?.label}` : ""}
          {searchQuery.trim() ? ` · "${searchQuery.trim()}"` : ""}
        </p>
      )}

      {globalVisible && (
        <Card className="overflow-hidden border-slate-300/70 dark:border-slate-800">
          <CardHeader className="border-b border-slate-200/70 dark:border-slate-800">
            <CardTitle className="flex items-center gap-2 text-base">
              <Layers2 className="h-4 w-4 text-[#0078D4]" />
              Client-global profile
              {profilesLoading && <Badge variant="secondary" className="ml-auto">Refreshing…</Badge>}
            </CardTitle>
          </CardHeader>
          <CardContent className="p-0">
            {globalCell ? (
              <ScopeRow
                cell={globalCell}
                plansById={plansById}
                routingPoliciesById={routingPoliciesById}
                queued={queuedScopeKeys.includes(scopeKey(globalCell.target))}
                onOpenCell={onOpenCell}
                onToggleQueuedScope={onToggleQueuedScope}
              />
            ) : (
              <div className="p-4 text-sm text-muted-foreground">Client-global scope is unavailable.</div>
            )}
          </CardContent>
        </Card>
      )}

      {filteredSections.map(({ section, visibleOperationCells, apiCellVisible }) => {
        const showApiRow = filtersActive ? apiCellVisible : true
        const operationCellsToRender = filtersActive
          ? visibleOperationCells
          : (section.expanded ? section.operationCells : [])

        return (
        <Card key={section.api.id} className="overflow-hidden border-slate-300/70 dark:border-slate-800">
          <CardHeader className="border-b border-slate-200/70 dark:border-slate-800">
            <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
              <button type="button" onClick={() => onToggleApi(section.api)} className="flex min-w-0 items-center gap-3 text-left">
                {section.expanded ? <ChevronDown className="h-4 w-4 text-muted-foreground" /> : <ChevronRight className="h-4 w-4 text-muted-foreground" />}
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate text-base font-semibold">{section.api.displayName}</span>
                    {section.api.isCurrent && <Badge className="flex-shrink-0" variant="green">Current</Badge>}
                  </div>
                  <p className="truncate text-xs text-muted-foreground font-mono">/{section.api.path}</p>
                </div>
              </button>
              <div className="flex flex-wrap items-center gap-2">
                <Badge variant="secondary">{section.directOverrideCount} direct override{section.directOverrideCount === 1 ? "" : "s"}</Badge>
                <Badge variant="outline">{section.operationCells.length} operation{section.operationCells.length === 1 ? "" : "s"}</Badge>
              </div>
            </div>
          </CardHeader>
          <CardContent className="p-0">
            {showApiRow && (
              <ScopeRow
                cell={section.apiCell}
                plansById={plansById}
                routingPoliciesById={routingPoliciesById}
                queued={queuedScopeKeys.includes(scopeKey(section.apiCell.target))}
                onOpenCell={onOpenCell}
                onToggleQueuedScope={onToggleQueuedScope}
              />
            )}

            {filtersActive && !showApiRow && operationCellsToRender.length === 0 && (
              <div className="px-4 py-4 text-sm text-muted-foreground">
                Overrides exist on operations within this API. Expand to view them.
              </div>
            )}

            {!filtersActive && section.expanded && section.loadingOperations && (
              <div className="border-t border-slate-200/70 px-4 py-4 text-sm text-muted-foreground dark:border-slate-800">
                Loading operations…
              </div>
            )}

            {!filtersActive && section.expanded && section.operationError && (
              <div className="border-t border-slate-200/70 px-4 py-4 dark:border-slate-800">
                <div className="flex items-center gap-3 rounded-xl border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
                  <span className="min-w-0 flex-1">{section.operationError}</span>
                  <Button type="button" variant="ghost" size="sm" onClick={() => onRetryOperations(section.api)}>
                    <RefreshCcw className="h-3.5 w-3.5" />
                    Retry
                  </Button>
                </div>
              </div>
            )}

            {!filtersActive && section.expanded && !section.loadingOperations && !section.operationError && section.operationCells.length === 0 && (
              <div className="border-t border-slate-200/70 px-4 py-4 text-sm text-muted-foreground dark:border-slate-800">
                No APIM operations were returned for this API.
              </div>
            )}

            {operationCellsToRender.length > 0 && (
              <div className="border-t border-slate-200/70 dark:border-slate-800">
                {operationCellsToRender.map((cell) => (
                  <ScopeRow
                    key={scopeKey(cell.target)}
                    cell={cell}
                    plansById={plansById}
                    routingPoliciesById={routingPoliciesById}
                    queued={queuedScopeKeys.includes(scopeKey(cell.target))}
                    onOpenCell={onOpenCell}
                    onToggleQueuedScope={onToggleQueuedScope}
                  />
                ))}
              </div>
            )}
          </CardContent>
        </Card>
        )
      })}

      {filtersActive && filteredSections.length === 0 && !globalVisible && (
        <Card>
          <CardContent className="flex min-h-[240px] flex-col items-center justify-center gap-3 text-center text-muted-foreground">
            <Search className="h-9 w-9 text-[#0078D4]" />
            <div>
              <p className="font-medium text-foreground">No scopes match your filters</p>
              <p className="text-sm">Try a different search term or change the override filter.</p>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={onClearFilters}>
              <X className="h-3.5 w-3.5" />
              Clear filters
            </Button>
          </CardContent>
        </Card>
      )}

      {sections.length === 0 && (
        <Card>
          <CardContent className="flex min-h-[240px] flex-col items-center justify-center gap-3 text-center text-muted-foreground">
            <Sparkles className="h-9 w-9 text-[#0078D4]" />
            <div>
              <p className="font-medium text-foreground">No APIs discovered</p>
              <p className="text-sm">APIM has not returned any APIs yet, so there are no scopes to manage for this client.</p>
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  )
}

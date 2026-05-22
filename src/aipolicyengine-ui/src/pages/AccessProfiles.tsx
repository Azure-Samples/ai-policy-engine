import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { useMsal } from "@azure/msal-react"
import { AlertTriangle, RefreshCcw, ShieldCheck, Sparkles } from "lucide-react"
import { ClientList } from "../components/accessProfiles/ClientList"
import { ProfileEditor, type ProfileEditorValues } from "../components/accessProfiles/ProfileEditor"
import { ProfileGrid } from "../components/accessProfiles/ProfileGrid"
import type { AccessGridCellData, AccessScopeTarget, EffectiveAccessPreview } from "../components/accessProfiles/types"
import { Button } from "../components/ui/button"
import { fetchClients, fetchDeployments, fetchPlans, fetchRoutingPolicies } from "../api"
import {
  bulkCreateAccessProfiles,
  createAccessProfile,
  deleteAccessProfile,
  fetchAccessProfile,
  fetchAccessProfiles,
  updateAccessProfile,
} from "../api/accessProfiles"
import { useApimCatalog } from "../hooks/useApimCatalog"
import type { ClientAssignment, DeploymentInfo, ModelRoutingPolicy, PlanData } from "../types"
import type { ApimApiSummary } from "../types/apim"
import type { AccessProfile } from "../types/accessProfiles"

interface ToastState {
  message: string
  retryLabel?: string
  onRetry?: () => void
}

interface EditorState {
  mode: "single" | "bulk"
  targets: AccessScopeTarget[]
  existingProfile: AccessProfile | null
  initialValues: ProfileEditorValues
}

const GLOBAL_API_ID = "_global"

function buildClientKey(client: Pick<ClientAssignment, "clientAppId" | "tenantId">): string {
  return `${client.clientAppId}|${client.tenantId}`
}

function parseClientKey(clientKey: string): { clientAppId: string; tenantId: string } {
  const [clientAppId = "", tenantId = ""] = clientKey.split("|")
  return { clientAppId, tenantId }
}

function buildScopeKey(apiId: string, operationId: string | null): string {
  return `${apiId}:${operationId ?? "_all"}`
}

function buildInitialValues(profile: AccessProfile | null, effective: EffectiveAccessPreview | null): ProfileEditorValues {
  if (profile) {
    return {
      planId: profile.planId,
      routingPolicyId: profile.routingPolicyId,
      allowedDeployments: profile.allowedDeployments,
      blocked: profile.blocked,
      enabled: profile.enabled,
    }
  }

  return {
    planId: effective?.planId ?? "",
    routingPolicyId: effective?.routingPolicyId ?? null,
    allowedDeployments: effective?.allowedDeployments ?? [],
    blocked: effective?.blocked ?? false,
    enabled: effective?.enabled ?? true,
  }
}

function createDirectPreview(
  profile: AccessProfile,
  plansById: Record<string, PlanData>,
): EffectiveAccessPreview {
  const plan = plansById[profile.planId]

  return {
    source: "direct",
    sourceLabel: "Direct override",
    sourceDescription: profile.blocked
      ? "This scope is blocked — all requests are denied with 403 Forbidden."
      : profile.enabled
        ? "This scope has its own Access Profile."
        : "This scope has a stored override, but it is disabled and no longer wins in the cascade.",
    profileId: profile.id,
    planId: profile.planId,
    routingPolicyId: profile.routingPolicyId ?? plan?.modelRoutingPolicyId ?? null,
    allowedDeployments: profile.allowedDeployments.length > 0 ? profile.allowedDeployments : (plan?.allowedDeployments ?? []),
    blocked: profile.blocked,
    enabled: profile.enabled,
  }
}

function createInheritedPreview(
  source: "api" | "global" | "client",
  payload: { id?: string | null; planId: string; routingPolicyId: string | null; allowedDeployments: string[]; blocked?: boolean },
  plansById: Record<string, PlanData>,
): EffectiveAccessPreview {
  const plan = plansById[payload.planId]

  return {
    source,
    sourceLabel: source === "api" ? "API-wide" : source === "global" ? "Client-global" : "Client assignment",
    sourceDescription:
      payload.blocked
        ? "Inherited block — all requests are denied at this scope."
        : source === "api"
          ? "Inherited from the API-wide override for this client."
          : source === "global"
            ? "Inherited from the client-global default for this client."
            : "Falling back to the client's base plan assignment.",
    profileId: payload.id ?? null,
    planId: payload.planId,
    routingPolicyId: payload.routingPolicyId ?? plan?.modelRoutingPolicyId ?? null,
    allowedDeployments: payload.allowedDeployments.length > 0 ? payload.allowedDeployments : (plan?.allowedDeployments ?? []),
    blocked: payload.blocked ?? false,
    enabled: true,
  }
}

export function AccessProfiles() {
  const { accounts } = useMsal()
  const [clients, setClients] = useState<ClientAssignment[]>([])
  const [plans, setPlans] = useState<PlanData[]>([])
  const [routingPolicies, setRoutingPolicies] = useState<ModelRoutingPolicy[]>([])
  const [deployments, setDeployments] = useState<DeploymentInfo[]>([])
  const [referenceLoading, setReferenceLoading] = useState(true)
  const [referenceError, setReferenceError] = useState<string | null>(null)
  const [profiles, setProfiles] = useState<AccessProfile[]>([])
  const [profilesLoading, setProfilesLoading] = useState(false)
  const [profilesError, setProfilesError] = useState<string | null>(null)
  const [selectedClientKey, setSelectedClientKey] = useState("")
  const [expandedApiIds, setExpandedApiIds] = useState<string[]>([])
  const [editorState, setEditorState] = useState<EditorState | null>(null)
  const [queuedScopeKeys, setQueuedScopeKeys] = useState<string[]>([])
  const [savingEditor, setSavingEditor] = useState(false)
  const [deletingEditor, setDeletingEditor] = useState(false)
  const [toast, setToast] = useState<ToastState | null>(null)
  const [accessDenied, setAccessDenied] = useState(false)

  const selectedClientKeyRef = useRef("")
  const adminRoleClaims = useMemo(() => {
    const roles = accounts[0]?.idTokenClaims?.roles
    return Array.isArray(roles) ? roles : []
  }, [accounts])
  const lacksExplicitAdminRole = adminRoleClaims.length > 0 && !adminRoleClaims.includes("AIPolicy.Admin")

  const {
    apis,
    catalogLoading,
    catalogError,
    accessDenied: catalogAccessDenied,
    operationsByApi,
    operationErrors,
    loadingOperationApiIds,
    refreshApis,
    ensureOperationsLoaded,
    refreshOperations,
  } = useApimCatalog({ enabled: !lacksExplicitAdminRole })

  useEffect(() => {
    selectedClientKeyRef.current = selectedClientKey
  }, [selectedClientKey])

  const showToast = useCallback((message: string, onRetry?: () => void, retryLabel = "Retry") => {
    setToast({ message, onRetry, retryLabel: onRetry ? retryLabel : undefined })
  }, [])

  const plansById = useMemo(
    () => Object.fromEntries(plans.map((plan) => [plan.id, plan])),
    [plans],
  )
  const routingPoliciesById = useMemo(
    () => Object.fromEntries(routingPolicies.map((policy) => [policy.id, policy])),
    [routingPolicies],
  )
  const profilesByScope = useMemo(
    () => Object.fromEntries(profiles.map((profile) => [buildScopeKey(profile.apiId, profile.operationId), profile])),
    [profiles],
  )
  const selectedClient = useMemo(
    () => clients.find((client) => buildClientKey(client) === selectedClientKey) ?? null,
    [clients, selectedClientKey],
  )

  const accessDeniedMessage = lacksExplicitAdminRole || accessDenied || catalogAccessDenied
    ? "You need AIPolicy.Admin role to use this page"
    : null

  const loadReferenceData = useCallback(async (): Promise<string | null> => {
    setReferenceLoading(true)
    setReferenceError(null)

    try {
      const [clientsResponse, plansResponse, routingPoliciesResponse, deploymentsResponse] = await Promise.all([
        fetchClients(),
        fetchPlans(),
        fetchRoutingPolicies().catch(() => ({ policies: [] })),
        fetchDeployments().catch(() => ({ deployments: [] })),
      ])

      const nextClients = clientsResponse.clients ?? []
      const nextSelectedClientKey = (() => {
        const currentKey = selectedClientKeyRef.current
        if (currentKey && nextClients.some((client) => buildClientKey(client) === currentKey)) {
          return currentKey
        }

        return nextClients[0] ? buildClientKey(nextClients[0]) : null
      })()

      setClients(nextClients)
      setPlans(plansResponse.plans ?? [])
      setRoutingPolicies(routingPoliciesResponse.policies ?? [])
      setDeployments(deploymentsResponse.deployments ?? [])
      setSelectedClientKey(nextSelectedClientKey ?? "")
      setAccessDenied(false)
      return nextSelectedClientKey
    } catch (error) {
      const status = typeof error === "object" && error !== null && "status" in error ? Number((error as { status?: number }).status) : undefined
      if (status === 401 || status === 403) {
        setAccessDenied(true)
        return null
      }

      const message = error instanceof Error ? error.message : "Failed to load reference data"
      setReferenceError(message)
      showToast(message, () => {
        void loadReferenceData()
      })
      return null
    } finally {
      setReferenceLoading(false)
    }
  }, [showToast])

  const loadProfiles = useCallback(async (clientKey: string) => {
    const { clientAppId, tenantId } = parseClientKey(clientKey)
    if (!clientAppId || !tenantId) {
      setProfiles([])
      return
    }

    setProfilesLoading(true)
    setProfilesError(null)

    try {
      const response = await fetchAccessProfiles({ clientAppId, tenantId })
      setProfiles(response.profiles ?? [])
      setAccessDenied(false)
    } catch (error) {
      const status = typeof error === "object" && error !== null && "status" in error ? Number((error as { status?: number }).status) : undefined
      if (status === 401 || status === 403) {
        setAccessDenied(true)
        return
      }

      const message = error instanceof Error ? error.message : "Failed to load access profiles"
      setProfilesError(message)
      showToast(message, () => {
        void loadProfiles(clientKey)
      })
    } finally {
      setProfilesLoading(false)
    }
  }, [showToast])

  useEffect(() => {
    if (lacksExplicitAdminRole) return

    void (async () => {
      await loadReferenceData()
    })()
  }, [lacksExplicitAdminRole, loadReferenceData])

  useEffect(() => {
    if (!selectedClientKey || accessDeniedMessage) return

    void (async () => {
      await loadProfiles(selectedClientKey)
    })()
  }, [selectedClientKey, accessDeniedMessage, loadProfiles])

  const resolveCell = useCallback((target: AccessScopeTarget): AccessGridCellData => {
    const directProfile = (profilesByScope[buildScopeKey(target.apiId, target.operationId)] ?? null) as AccessProfile | null
    if (directProfile) {
      return {
        target,
        directProfile,
        effective: createDirectPreview(directProfile, plansById),
      }
    }

    const apiProfile = target.kind === "global" ? null : profilesByScope[buildScopeKey(target.apiId, null)] ?? null
    const globalProfile = profilesByScope[buildScopeKey(GLOBAL_API_ID, null)] ?? null

    if (target.kind === "operation" && apiProfile?.enabled) {
      return {
        target,
        directProfile: null,
        effective: createInheritedPreview("api", apiProfile, plansById),
      }
    }

    if (target.kind !== "global" && globalProfile?.enabled) {
      return {
        target,
        directProfile: null,
        effective: createInheritedPreview("global", globalProfile, plansById),
      }
    }

    if (selectedClient) {
      return {
        target,
        directProfile: null,
        effective: createInheritedPreview(
          "client",
          {
            id: null,
            planId: selectedClient.planId,
            routingPolicyId: selectedClient.modelRoutingPolicyOverride ?? null,
            allowedDeployments: selectedClient.allowedDeployments ?? [],
          },
          plansById,
        ),
      }
    }

    return {
      target,
      directProfile: null,
      effective: null,
    }
  }, [plansById, profilesByScope, selectedClient])

  const sections = useMemo(() => {
    return apis.map((api) => {
      const operationCells = (operationsByApi[api.id] ?? []).map((operation) => resolveCell({
        kind: "operation",
        apiId: api.id,
        apiDisplayName: api.displayName,
        operationId: operation.id,
        operationDisplayName: operation.displayName,
        method: operation.method,
        urlTemplate: operation.urlTemplate,
      }))

      return {
        api,
        apiCell: resolveCell({
          kind: "api",
          apiId: api.id,
          apiDisplayName: api.displayName,
          operationId: null,
        }),
        operationCells,
        directOverrideCount: profiles.filter((profile) => profile.apiId === api.id).length,
        expanded: expandedApiIds.includes(api.id),
        loadingOperations: loadingOperationApiIds.includes(api.id),
        operationError: operationErrors[api.id] ?? null,
      }
    })
  }, [apis, expandedApiIds, loadingOperationApiIds, operationErrors, operationsByApi, profiles, resolveCell])

  const globalCell = useMemo(() => resolveCell({
    kind: "global",
    apiId: GLOBAL_API_ID,
    apiDisplayName: "Client-global",
    operationId: null,
  }), [resolveCell])

  const allCellsByScopeKey = useMemo(() => {
    const entries: Array<[string, AccessGridCellData]> = [[buildScopeKey(globalCell.target.apiId, globalCell.target.operationId), globalCell]]

    for (const section of sections) {
      entries.push([buildScopeKey(section.apiCell.target.apiId, section.apiCell.target.operationId), section.apiCell])
      for (const operationCell of section.operationCells) {
        entries.push([buildScopeKey(operationCell.target.apiId, operationCell.target.operationId), operationCell])
      }
    }

    return Object.fromEntries(entries)
  }, [globalCell, sections])

  const handleSelectClient = (clientKey: string) => {
    setSelectedClientKey(clientKey)
    setExpandedApiIds([])
    setQueuedScopeKeys([])
    setEditorState(null)
  }

  const handleToggleApi = (api: ApimApiSummary) => {
    setExpandedApiIds((current) => {
      const isExpanded = current.includes(api.id)
      if (isExpanded) {
        return current.filter((item) => item !== api.id)
      }

      return [...current, api.id]
    })

    if (!operationsByApi[api.id]) {
      void ensureOperationsLoaded(api)
    }
  }

  const handleOpenCell = (target: AccessScopeTarget, directProfile: AccessProfile | null, effective: EffectiveAccessPreview | null) => {
    void (async () => {
      if (directProfile) {
        try {
          const freshProfile = await fetchAccessProfile(directProfile.id)
          setEditorState({
            mode: "single",
            targets: [target],
            existingProfile: freshProfile,
            initialValues: buildInitialValues(freshProfile, effective),
          })
        } catch (error) {
          const message = error instanceof Error ? error.message : "Failed to load the latest Access Profile"
          showToast(message)
        }
        return
      }

      setEditorState({
        mode: "single",
        targets: [target],
        existingProfile: null,
        initialValues: buildInitialValues(null, effective),
      })
    })()
  }

  const handleToggleQueuedScope = (target: AccessScopeTarget) => {
    const scopeKey = buildScopeKey(target.apiId, target.operationId)
    setQueuedScopeKeys((current) => current.includes(scopeKey)
      ? current.filter((item) => item !== scopeKey)
      : [...current, scopeKey])
  }

  const handleOpenBulkEditor = () => {
    const selectedCells = queuedScopeKeys
      .map((scopeKey) => allCellsByScopeKey[scopeKey])
      .filter((cell): cell is AccessGridCellData => Boolean(cell))

    if (selectedCells.length === 0) return

    setEditorState({
      mode: "bulk",
      targets: selectedCells.map((cell) => cell.target),
      existingProfile: null,
      initialValues: buildInitialValues(null, selectedCells[0].effective),
    })
  }

  const handleSaveEditor = async (values: ProfileEditorValues) => {
    if (!selectedClient || !editorState) return

    setSavingEditor(true)
    try {
      if (editorState.mode === "bulk") {
        const payload = {
          profiles: editorState.targets.map((target) => ({
            clientAppId: selectedClient.clientAppId,
            tenantId: selectedClient.tenantId,
            apiId: target.apiId,
            operationId: target.operationId,
            planId: values.planId || undefined,
            routingPolicyId: values.routingPolicyId,
            allowedDeployments: values.allowedDeployments,
            blocked: values.blocked,
            enabled: values.enabled,
          })),
        }

        const response = await bulkCreateAccessProfiles(payload)
        const failureCount = response.failed.length
        if (failureCount > 0) {
          showToast(`Created ${response.created} override${response.created === 1 ? "" : "s"}; ${failureCount} failed.`)
        } else {
          showToast(`Created ${response.created} access override${response.created === 1 ? "" : "s"}.`)
        }
        setQueuedScopeKeys([])
      } else if (editorState.existingProfile) {
        await updateAccessProfile(editorState.existingProfile.id, {
          ...values,
          routingPolicyId: values.routingPolicyId,
          planId: values.planId || undefined,
        })
        showToast("Access Profile updated.")
      } else {
        const target = editorState.targets[0]
        await createAccessProfile({
          clientAppId: selectedClient.clientAppId,
          tenantId: selectedClient.tenantId,
          apiId: target.apiId,
          operationId: target.operationId,
          planId: values.planId || undefined,
          routingPolicyId: values.routingPolicyId,
          allowedDeployments: values.allowedDeployments,
          blocked: values.blocked,
          enabled: values.enabled,
        })
        showToast("Access Profile created.")
      }

      setEditorState(null)
      await loadProfiles(buildClientKey(selectedClient))
    } catch (error) {
      const message = error instanceof Error ? error.message : "Failed to save Access Profile"
      showToast(message)
    } finally {
      setSavingEditor(false)
    }
  }

  const handleDeleteEditor = async () => {
    if (!editorState?.existingProfile || !selectedClient) return

    setDeletingEditor(true)
    try {
      await deleteAccessProfile(editorState.existingProfile.id)
      setEditorState(null)
      showToast("Access Profile deleted.")
      await loadProfiles(buildClientKey(selectedClient))
    } catch (error) {
      const message = error instanceof Error ? error.message : "Failed to delete Access Profile"
      showToast(message)
    } finally {
      setDeletingEditor(false)
    }
  }

  const handleRefresh = () => {
    void (async () => {
      const nextClientKey = await loadReferenceData()
      await refreshApis()
      const clientKeyToUse = nextClientKey ?? selectedClientKeyRef.current
      if (clientKeyToUse) {
        await loadProfiles(clientKeyToUse)
      }
    })()
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="flex items-center gap-3">
            <ShieldCheck className="h-7 w-7 text-[#0078D4]" />
            <h2 className="text-2xl font-bold tracking-tight">Access Profiles</h2>
          </div>
          <p className="mt-2 max-w-3xl text-sm text-muted-foreground">
            Configure client-global, API-wide, and operation-level access overrides. Empty cells visualize the active cascade before you commit a direct override.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {queuedScopeKeys.length > 0 && (
            <Button type="button" variant="outline" onClick={handleOpenBulkEditor}>
              Bulk override {queuedScopeKeys.length} selected
            </Button>
          )}
          <Button type="button" variant="outline" onClick={handleRefresh} disabled={referenceLoading || catalogLoading || profilesLoading}>
            <RefreshCcw className="h-4 w-4" />
            Refresh
          </Button>
        </div>
      </div>

      {accessDeniedMessage && (
        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
          <div className="flex items-start gap-3">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" />
            <div>
              <p className="font-medium">{accessDeniedMessage}</p>
              <p className="mt-1 text-sm">Ask an administrator to grant the AIPolicy.Admin role, then refresh this page.</p>
            </div>
          </div>
        </div>
      )}

      {!accessDeniedMessage && (referenceError || catalogError || profilesError) && (
        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
          <div className="flex items-center gap-2">
            <AlertTriangle className="h-4 w-4" />
            <span>{referenceError ?? catalogError ?? profilesError}</span>
            <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={handleRefresh}>
              Retry
            </Button>
          </div>
        </div>
      )}

      {!accessDeniedMessage && (
        <div className="grid gap-6 xl:grid-cols-[320px_minmax(0,1fr)]">
          <div className="min-h-[520px]">
            {referenceLoading ? (
              <div className="flex h-full min-h-[520px] items-center justify-center rounded-xl border border-dashed text-sm text-muted-foreground">
                Loading clients…
              </div>
            ) : (
              <ClientList
                clients={clients}
                plans={plans}
                selectedClientKey={selectedClientKey}
                onSelectClient={handleSelectClient}
              />
            )}
          </div>

          <div>
            {catalogLoading && apis.length === 0 ? (
              <div className="flex min-h-[520px] items-center justify-center rounded-xl border border-dashed text-sm text-muted-foreground">
                Loading APIs…
              </div>
            ) : (
              <ProfileGrid
                client={selectedClient}
                globalCell={globalCell}
                sections={sections}
                plansById={plansById}
                routingPoliciesById={routingPoliciesById}
                queuedScopeKeys={queuedScopeKeys}
                profilesLoading={profilesLoading}
                onToggleApi={handleToggleApi}
                onRetryOperations={(api) => { void refreshOperations(api) }}
                onOpenCell={handleOpenCell}
                onToggleQueuedScope={handleToggleQueuedScope}
              />
            )}
          </div>
        </div>
      )}

      {editorState && !accessDeniedMessage && (
        <ProfileEditor
          key={`${editorState.mode}:${editorState.targets.map((target) => buildScopeKey(target.apiId, target.operationId)).join(",")}:${editorState.existingProfile?.id ?? "new"}`}
          open
          mode={editorState.mode}
          targets={editorState.targets}
          existingProfile={editorState.existingProfile}
          initialValues={editorState.initialValues}
          plans={plans}
          routingPolicies={routingPolicies}
          deployments={deployments}
          saving={savingEditor}
          deleting={deletingEditor}
          onClose={() => setEditorState(null)}
          onSave={handleSaveEditor}
          onDelete={editorState.existingProfile ? handleDeleteEditor : undefined}
        />
      )}

      {toast && (
        <div className="fixed bottom-4 right-4 z-50 w-full max-w-sm rounded-xl border bg-card p-4 shadow-lg">
          <div className="flex items-start gap-3">
            <Sparkles className="mt-0.5 h-4 w-4 shrink-0 text-[#0078D4]" />
            <div className="min-w-0 flex-1">
              <p className="text-sm">{toast.message}</p>
              <div className="mt-3 flex justify-end gap-2">
                {toast.onRetry && (
                  <Button type="button" variant="outline" size="sm" onClick={toast.onRetry}>
                    {toast.retryLabel ?? "Retry"}
                  </Button>
                )}
                <Button type="button" variant="ghost" size="sm" onClick={() => setToast(null)}>
                  Dismiss
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

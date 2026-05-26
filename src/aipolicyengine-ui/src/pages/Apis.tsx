import { useCallback, useEffect, useMemo, useState } from "react"
import { useMsal } from "@azure/msal-react"
import { AlertTriangle, Network, RefreshCcw } from "lucide-react"
import { ApiTree } from "../components/apis/ApiTree"
import { AssignTemplateForm } from "../components/apis/AssignTemplateForm"
import { PolicyAssignmentPanel } from "../components/apis/PolicyAssignmentPanel"
import { Badge } from "../components/ui/badge"
import { Button } from "../components/ui/button"
import { fetchPlans } from "../api"
import {
  applyApiPolicy,
  applyOperationPolicy,
  clearApiPolicy,
  clearOperationPolicy,
  fetchApiPolicy,
  fetchApimTemplates,
  fetchOperationPolicy,
} from "../api/apim"
import { useApimCatalog } from "../hooks/useApimCatalog"
import type { PlanData } from "../types"
import type {
  ApimApiSummary,
  ApimOperationSummary,
  ApimTemplateSummary,
  ApplyPolicyRequest,
  HttpError,
  PolicyAssignmentStatus,
  PolicyDocumentResponse,
} from "../types/apim"

interface SelectedApiTarget {
  kind: "api"
  api: ApimApiSummary
}

interface SelectedOperationTarget {
  kind: "operation"
  api: ApimApiSummary
  operation: ApimOperationSummary
}

type SelectedTarget = SelectedApiTarget | SelectedOperationTarget

interface ToastState {
  message: string
  retryLabel?: string
  onRetry?: () => void
}

const PLAN_PARAMETER_RESOLVERS: Record<string, (plan: PlanData) => number | undefined> = {
  NonAiRequestsPerMinute: (plan) => plan.requestsPerMinuteLimit,
  RequestsPerMinuteLimit: (plan) => plan.requestsPerMinuteLimit,
  NonAiMonthlyRequestQuota: (plan) => plan.monthlyRequestQuota,
  MonthlyRequestQuota: (plan) => plan.monthlyRequestQuota,
  TokensPerMinuteLimit: (plan) => plan.tokensPerMinuteLimit,
  MonthlyTokenQuota: (plan) => plan.monthlyTokenQuota,
}

function targetKey(target: SelectedTarget): string {
  return target.kind === "api" ? `api:${target.api.id}` : `operation:${target.api.id}:${target.operation.id}`
}

function targetSummary(target: SelectedTarget) {
  if (target.kind === "api") {
    return {
      key: targetKey(target),
      kind: "api" as const,
      title: target.api.displayName,
      subtitle: `/${target.api.path}`,
    }
  }

  return {
    key: targetKey(target),
    kind: "operation" as const,
    title: target.operation.displayName,
    subtitle: `${target.operation.method} ${target.operation.urlTemplate}`,
  }
}

function getStatus(error: unknown): number | undefined {
  return typeof error === "object" && error !== null && "status" in error
    ? (error as HttpError).status
    : undefined
}

function getErrorMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

function isPollingStatus(status?: PolicyAssignmentStatus): boolean {
  return status === "pending" || status === "applying"
}

function createApplyingPolicyDocument(
  current: PolicyDocumentResponse | null,
  target: SelectedTarget,
  payload: ApplyPolicyRequest,
  template: ApimTemplateSummary | undefined,
  assignmentId: string,
  appliedBy: string,
): PolicyDocumentResponse {
  const now = new Date().toISOString()

  return {
    assignment: {
      id: assignmentId,
      apiId: target.api.id,
      operationId: target.kind === "api" ? null : target.operation.id,
      apiDisplayName: target.api.displayName,
      templateId: payload.templateId,
      templateVersion: template?.version ?? current?.assignment?.templateVersion ?? "",
      parameters: payload.parameters,
      generatedXmlHash: current?.assignment?.generatedXmlHash ?? null,
      lastAppliedAt: current?.assignment?.lastAppliedAt ?? null,
      appliedBy: current?.assignment?.appliedBy || appliedBy,
      status: "applying",
      errorMessage: null,
      createdAt: current?.assignment?.createdAt ?? now,
      updatedAt: now,
    },
    currentXml: current?.currentXml ?? "",
  }
}

// TODO(contract): the backend contract does not identify which plan's defaults should hydrate API assignment forms, so only values shared across plans are prefilled.
function derivePlanDefaults(plans: PlanData[]): Record<string, number> {
  const defaults: Record<string, number> = {}

  for (const [parameterName, resolver] of Object.entries(PLAN_PARAMETER_RESOLVERS)) {
    const resolvedValues = plans
      .map((plan) => resolver(plan))
      .filter((value): value is number => value !== undefined)

    if (resolvedValues.length === 0) continue

    const uniqueValues = Array.from(new Set(resolvedValues))
    if (uniqueValues.length === 1) {
      defaults[parameterName] = uniqueValues[0]
    }
  }

  return defaults
}

export function Apis() {
  const { accounts } = useMsal()
  const [templates, setTemplates] = useState<ApimTemplateSummary[]>([])
  const [plans, setPlans] = useState<PlanData[]>([])
  const [initialLoading, setInitialLoading] = useState(true)
  const [initialError, setInitialError] = useState<string | null>(null)
  const [accessDeniedMessage, setAccessDeniedMessage] = useState<string | null>(null)
  const [toast, setToast] = useState<ToastState | null>(null)

  const [expandedApiIds, setExpandedApiIds] = useState<string[]>([])

  const [selectedTarget, setSelectedTarget] = useState<SelectedTarget | null>(null)
  const [policyDocument, setPolicyDocument] = useState<PolicyDocumentResponse | null>(null)
  const [policyLoading, setPolicyLoading] = useState(false)
  const [policyError, setPolicyError] = useState<string | null>(null)

  const [assignFormOpen, setAssignFormOpen] = useState(false)
  const [submittingAssignment, setSubmittingAssignment] = useState(false)
  const [clearingAssignment, setClearingAssignment] = useState(false)

  const adminRoleClaims = useMemo(() => {
    const firstAccount = accounts[0]
    const roles = firstAccount?.idTokenClaims?.roles
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
  const effectiveAccessDeniedMessage = accessDeniedMessage ?? (catalogAccessDenied ? "You need AIPolicy.Admin role to use this page" : null)
  const selectedSummary = selectedTarget ? targetSummary(selectedTarget) : null
  const selectedKey = selectedTarget ? targetKey(selectedTarget) : undefined
  const busy = submittingAssignment || clearingAssignment
  const planDefaults = useMemo(() => derivePlanDefaults(plans), [plans])
  const pageLoading = initialLoading || catalogLoading
  const pageError = initialError ?? catalogError

  const showToast = useCallback((message: string, onRetry?: () => void, retryLabel = "Retry") => {
    setToast({ message, onRetry, retryLabel: onRetry ? retryLabel : undefined })
  }, [])

  const handleAccessError = useCallback(() => {
    setAccessDeniedMessage("You need AIPolicy.Admin role to use this page")
  }, [])

  const loadPolicy = useCallback(async (target: SelectedTarget, options?: { silent?: boolean }) => {
    if (!options?.silent) {
      setPolicyLoading(true)
      setPolicyError(null)
    }

    try {
      const nextPolicyDocument = target.kind === "api"
        ? await fetchApiPolicy(target.api.id)
        : await fetchOperationPolicy(target.api.id, target.operation.id)

      setPolicyDocument(nextPolicyDocument)
      setPolicyError(null)
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        handleAccessError()
        setPolicyDocument(null)
        return
      }

      const message = getErrorMessage(error, "Failed to load policy details")
      setPolicyError(message)
      showToast(message, () => {
        void loadPolicy(target)
      })
    } finally {
      if (!options?.silent) {
        setPolicyLoading(false)
      }
    }
  }, [handleAccessError, showToast])

  const loadInitialData = useCallback(async () => {
    setInitialLoading(true)
    setInitialError(null)
    setAccessDeniedMessage(null)

    try {
      const [templatesResponse, plansResponse] = await Promise.all([
        fetchApimTemplates(),
        fetchPlans().catch(() => ({ plans: [] })),
        refreshApis(),
      ])

      setTemplates(templatesResponse.templates ?? [])
      setPlans(plansResponse.plans ?? [])
      setExpandedApiIds([])
      setInitialError(null)
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        handleAccessError()
        setTemplates([])
        setPlans([])
        return
      }

      const message = getErrorMessage(error, "Failed to load APIM data")
      setInitialError(message)
      showToast(message, () => {
        void loadInitialData()
      })
    } finally {
      setInitialLoading(false)
    }
  }, [handleAccessError, refreshApis, showToast])

  useEffect(() => {
    if (lacksExplicitAdminRole) {
      setAccessDeniedMessage("You need AIPolicy.Admin role to use this page")
      setInitialLoading(false)
      return
    }

    void loadInitialData()
  }, [lacksExplicitAdminRole, loadInitialData])

  useEffect(() => {
    setSelectedTarget((current) => {
      if (!current) {
        return apis[0] ? { kind: "api", api: apis[0] } : null
      }

      const matchingApi = apis.find((api) => api.id === current.api.id)
      if (!matchingApi) {
        return apis[0] ? { kind: "api", api: apis[0] } : null
      }

      if (current.kind === "api") {
        return { kind: "api", api: matchingApi }
      }

      const matchingOperation = (operationsByApi[matchingApi.id] ?? []).find((operation) => operation.id === current.operation.id)
      return matchingOperation
        ? { kind: "operation", api: matchingApi, operation: matchingOperation }
        : { kind: "api", api: matchingApi }
    })
  }, [apis, operationsByApi])

  useEffect(() => {
    if (!selectedTarget || effectiveAccessDeniedMessage) {
      setPolicyDocument(null)
      setPolicyError(null)
      return
    }

    void loadPolicy(selectedTarget)
  }, [effectiveAccessDeniedMessage, loadPolicy, selectedTarget])

  useEffect(() => {
    if (!selectedTarget || !isPollingStatus(policyDocument?.assignment?.status)) return

    const timeoutId = window.setTimeout(() => {
      void loadPolicy(selectedTarget, { silent: true })
    }, 2000)

    return () => window.clearTimeout(timeoutId)
  }, [loadPolicy, policyDocument?.assignment?.status, selectedTarget])

  const handleApiToggle = (api: ApimApiSummary) => {
    setExpandedApiIds((current) => {
      const alreadyExpanded = current.includes(api.id)
      if (alreadyExpanded) {
        return current.filter((apiId) => apiId !== api.id)
      }

      return [...current, api.id]
    })

    if (!operationsByApi[api.id]) {
      void ensureOperationsLoaded(api)
    }
  }

  const handleApiSelect = (api: ApimApiSummary) => {
    setSelectedTarget({ kind: "api", api })
    if (!expandedApiIds.includes(api.id)) {
      setExpandedApiIds((current) => [...current, api.id])
    }
    if (!operationsByApi[api.id]) {
      void ensureOperationsLoaded(api)
    }
  }

  const handleOperationSelect = (api: ApimApiSummary, operation: ApimOperationSummary) => {
    if (!expandedApiIds.includes(api.id)) {
      setExpandedApiIds((current) => [...current, api.id])
    }
    setSelectedTarget({ kind: "operation", api, operation })
  }

  const handleApplyTemplate = async (payload: ApplyPolicyRequest) => {
    if (!selectedTarget) return

    setSubmittingAssignment(true)

    try {
      const response = selectedTarget.kind === "api"
        ? await applyApiPolicy(selectedTarget.api.id, payload)
        : await applyOperationPolicy(selectedTarget.api.id, selectedTarget.operation.id, payload)

      const template = templates.find((item) => item.id === payload.templateId)
      const appliedBy = accounts[0]?.username ?? ""
      setPolicyDocument((current) => createApplyingPolicyDocument(current, selectedTarget, payload, template, response.assignmentId, appliedBy))
      setPolicyError(null)
      setAssignFormOpen(false)
      showToast(`Policy apply accepted (${response.status}). Polling for the latest status…`)
      void loadPolicy(selectedTarget, { silent: true })
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        handleAccessError()
        return
      }

      const message = getErrorMessage(error, "Failed to apply policy template")
      setPolicyError(message)
      showToast(message, () => {
        void handleApplyTemplate(payload)
      })
      throw error
    } finally {
      setSubmittingAssignment(false)
    }
  }

  const handleClearAssignment = async () => {
    if (!selectedTarget) return

    setClearingAssignment(true)
    try {
      if (selectedTarget.kind === "api") {
        await clearApiPolicy(selectedTarget.api.id)
      } else {
        await clearOperationPolicy(selectedTarget.api.id, selectedTarget.operation.id)
      }

      setPolicyDocument((current) => ({
        assignment: null,
        currentXml: current?.currentXml ?? "",
      }))
      setPolicyError(null)
      showToast("Policy assignment cleared.")
      void loadPolicy(selectedTarget, { silent: true })
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        handleAccessError()
        return
      }

      const message = getErrorMessage(error, "Failed to clear policy assignment")
      setPolicyError(message)
      showToast(message, () => {
        void handleClearAssignment()
      })
      throw error
    } finally {
      setClearingAssignment(false)
    }
  }

  if (effectiveAccessDeniedMessage) {
    return (
      <div className="space-y-6">
        <div className="flex items-center gap-3">
          <Network className="h-7 w-7 text-[#0078D4]" />
          <div>
            <h2 className="text-2xl font-bold tracking-tight">APIs</h2>
            <p className="text-sm text-muted-foreground">Manage APIM template assignments for APIs and operations.</p>
          </div>
        </div>

        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
          <div className="flex items-start gap-3">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" />
            <div>
              <p className="font-medium">{effectiveAccessDeniedMessage}</p>
              <p className="mt-1 text-sm">Ask an administrator to grant the AIPolicy.Admin role, then refresh this page.</p>
            </div>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div className="flex items-center gap-3">
          <Network className="h-7 w-7 text-[#0078D4]" />
          <div>
            <h2 className="text-2xl font-bold tracking-tight">APIs</h2>
            <p className="text-sm text-muted-foreground">
              Browse APIM APIs, inspect current assignments, and apply policy templates without editing XML.
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          {Object.keys(planDefaults).length > 0 && <Badge variant="blue">Plan defaults available</Badge>}
          <Button type="button" variant="outline" onClick={() => void loadInitialData()} disabled={pageLoading}>
            <RefreshCcw className={`h-4 w-4 ${pageLoading ? "animate-spin" : ""}`} />
            Refresh
          </Button>
        </div>
      </div>

      {pageError && (
        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
          <div className="flex items-center gap-2">
            <AlertTriangle className="h-4 w-4" />
            <span>{pageError}</span>
            <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={() => void loadInitialData()}>
              Retry
            </Button>
          </div>
        </div>
      )}

      <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="min-h-[520px]">
          {pageLoading ? (
            <div className="flex h-full min-h-[520px] items-center justify-center rounded-xl border border-dashed text-sm text-muted-foreground">
              Loading APIs…
            </div>
          ) : (
            <ApiTree
              apis={apis}
              expandedApiIds={expandedApiIds}
              loadingOperationApiIds={loadingOperationApiIds}
              operationsByApi={operationsByApi}
              operationErrors={operationErrors}
              selectedKey={selectedKey}
              onApiToggle={handleApiToggle}
              onApiSelect={handleApiSelect}
              onOperationSelect={handleOperationSelect}
              onRetryOperations={(api) => {
                void refreshOperations(api)
              }}
            />
          )}
        </div>

        <PolicyAssignmentPanel
          selectedTarget={selectedSummary}
          policyDocument={policyDocument}
          policyLoading={policyLoading}
          policyError={policyError}
          templates={templates}
          busy={busy}
          onAssign={() => setAssignFormOpen(true)}
          onClear={handleClearAssignment}
          onRetry={() => {
            if (selectedTarget) {
              void loadPolicy(selectedTarget)
            }
          }}
        />
      </div>

      {selectedTarget && (
        <AssignTemplateForm
          open={assignFormOpen}
          onOpenChange={setAssignFormOpen}
          targetKind={selectedTarget.kind}
          templates={templates}
          initialTemplateId={policyDocument?.assignment?.templateId}
          initialParameters={policyDocument?.assignment?.parameters}
          planDefaults={planDefaults}
          submitting={submittingAssignment}
          onSubmit={handleApplyTemplate}
        />
      )}

      {toast && (
        <div className="fixed bottom-4 right-4 z-50 w-full max-w-sm rounded-xl border bg-card p-4 shadow-lg">
          <div className="flex items-start gap-3">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-[#0078D4]" />
            <div className="min-w-0 flex-1">
              <p className="text-sm">{toast.message}</p>
              <div className="mt-3 flex items-center justify-end gap-2">
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

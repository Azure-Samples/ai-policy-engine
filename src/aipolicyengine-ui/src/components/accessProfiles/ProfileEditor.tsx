import { useMemo, useState } from "react"
import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { Dialog, DialogClose, DialogHeader, DialogTitle } from "../ui/dialog"
import { Input } from "../ui/input"
import { cn } from "../../lib/utils"
import type { DeploymentInfo, ModelRoutingPolicy, PlanData } from "../../types"
import type { AccessProfile } from "../../types/accessProfiles"
import type { AccessScopeTarget } from "./types"

export interface ProfileEditorValues {
  planId: string
  routingPolicyId: string | null
  allowedDeployments: string[]
  blocked: boolean
  enabled: boolean
}

interface ProfileEditorProps {
  open: boolean
  mode: "single" | "bulk"
  targets: AccessScopeTarget[]
  existingProfile: AccessProfile | null
  initialValues: ProfileEditorValues
  plans: PlanData[]
  routingPolicies: ModelRoutingPolicy[]
  deployments: DeploymentInfo[]
  saving: boolean
  deleting: boolean
  onClose: () => void
  onSave: (values: ProfileEditorValues) => Promise<void>
  onDelete?: () => Promise<void>
}

function targetLabel(target: AccessScopeTarget): string {
  if (target.kind === "global") return "Client-global default"
  if (target.kind === "api") return `${target.apiDisplayName} · API-wide`
  return `${target.apiDisplayName} · ${target.method} ${target.operationDisplayName ?? target.operationId}`
}

export function ProfileEditor({
  open,
  mode,
  targets,
  existingProfile,
  initialValues,
  plans,
  routingPolicies,
  deployments,
  saving,
  deleting,
  onClose,
  onSave,
  onDelete,
}: ProfileEditorProps) {
  const [planId, setPlanId] = useState(initialValues.planId)
  const [routingPolicyId, setRoutingPolicyId] = useState(initialValues.routingPolicyId ?? "")
  const [allowedDeployments, setAllowedDeployments] = useState<string[]>(initialValues.allowedDeployments)
  const [blocked, setBlocked] = useState(initialValues.blocked)
  const [enabled, setEnabled] = useState(initialValues.enabled)
  const [deploymentQuery, setDeploymentQuery] = useState("")
  const [formError, setFormError] = useState<string | null>(null)

  const filteredDeployments = useMemo(() => {
    const normalizedQuery = deploymentQuery.trim().toLowerCase()
    if (!normalizedQuery) return deployments

    return deployments.filter((deployment) => {
      return [deployment.id, deployment.model, deployment.modelVersion]
        .some((value) => value.toLowerCase().includes(normalizedQuery))
    })
  }, [deploymentQuery, deployments])

  const toggleDeployment = (deploymentId: string) => {
    setAllowedDeployments((current) => (
      current.includes(deploymentId)
        ? current.filter((item) => item !== deploymentId)
        : [...current, deploymentId]
    ))
  }

  const handleSave = async () => {
    if (!blocked && !planId) {
      setFormError("Select a plan before saving.")
      return
    }

    setFormError(null)
    await onSave({
      planId,
      routingPolicyId: routingPolicyId || null,
      allowedDeployments,
      blocked,
      enabled,
    })
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) => {
        if (!nextOpen) onClose()
      }}
      contentClassName="max-w-4xl lg:ml-auto lg:mr-0 lg:h-screen lg:max-h-screen lg:rounded-none lg:border-l"
    >
      <DialogClose onClose={onClose} />
      <DialogHeader className="pr-10">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant={mode === "bulk" ? "amber" : existingProfile ? "teal" : "cyan"}>
            {mode === "bulk" ? "Bulk create" : existingProfile ? "Edit override" : "Create override"}
          </Badge>
          <Badge variant="outline">{targets.length} scope{targets.length === 1 ? "" : "s"}</Badge>
        </div>
        <DialogTitle className="mt-2 text-xl">
          {mode === "bulk" ? "Apply direct overrides to selected scopes" : "Access Profile editor"}
        </DialogTitle>
        <p className="text-sm text-muted-foreground">
          Leave routing blank to inherit from the chosen plan. Leave deployments empty to use that plan's deployment rules.
        </p>
      </DialogHeader>

      <div className="mt-6 grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
        <div className="space-y-5">
          <section className="rounded-2xl border border-slate-300/70 bg-slate-500/5 p-4 dark:border-slate-800 dark:bg-slate-500/10">
            <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted-foreground">Targets</h3>
            <div className="mt-3 space-y-2">
              {targets.map((target) => (
                <div key={`${target.apiId}:${target.operationId ?? "_all"}`} className="rounded-xl border bg-background/70 px-3 py-2">
                  <p className="truncate text-sm font-medium">{targetLabel(target)}</p>
                  <p className="truncate text-xs text-muted-foreground font-mono">
                    {target.apiId}{target.operationId ? ` / ${target.operationId}` : " / _all"}
                  </p>
                </div>
              ))}
            </div>
          </section>

          <section className={cn("rounded-2xl border p-4", blocked && "opacity-50 pointer-events-none")}>
            <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted-foreground">Plan + routing</h3>
            <div className="mt-4 grid gap-4 md:grid-cols-2">
              <div className="space-y-2">
                <label htmlFor="access-profile-plan" className="text-sm font-medium">Plan</label>
                <select
                  id="access-profile-plan"
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                  value={planId}
                  onChange={(event) => setPlanId(event.target.value)}
                >
                  <option value="">Select a plan…</option>
                  {plans.map((plan) => (
                    <option key={plan.id} value={plan.id}>{plan.name}</option>
                  ))}
                </select>
              </div>

              <div className="space-y-2">
                <label htmlFor="access-profile-routing" className="text-sm font-medium">Routing policy</label>
                <select
                  id="access-profile-routing"
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                  value={routingPolicyId}
                  onChange={(event) => setRoutingPolicyId(event.target.value)}
                >
                  <option value="">Inherit from plan</option>
                  {routingPolicies.map((policy) => (
                    <option key={policy.id} value={policy.id}>{policy.name}</option>
                  ))}
                </select>
              </div>
            </div>
          </section>

          <section className={cn("rounded-2xl border p-4", blocked && "opacity-50 pointer-events-none")}>
            <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted-foreground">Allowed deployments</h3>
                <p className="mt-1 text-sm text-muted-foreground">Choose specific deployments or leave the list empty to inherit from the selected plan.</p>
              </div>
              <div className="flex gap-2">
                <Button type="button" variant="outline" size="sm" onClick={() => setAllowedDeployments([])}>
                  Use plan defaults
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => setAllowedDeployments(filteredDeployments.map((deployment) => deployment.id))}
                >
                  Select visible
                </Button>
              </div>
            </div>

            <div className="mt-4 space-y-3">
              <Input
                value={deploymentQuery}
                onChange={(event) => setDeploymentQuery(event.target.value)}
                placeholder="Filter deployments…"
              />
              <div className="grid max-h-[280px] gap-2 overflow-auto rounded-xl border border-dashed p-3 md:grid-cols-2">
                {filteredDeployments.length === 0 ? (
                  <div className="col-span-full text-sm text-muted-foreground">No deployments match the current filter.</div>
                ) : (
                  filteredDeployments.map((deployment) => {
                    const checked = allowedDeployments.includes(deployment.id)
                    return (
                      <label
                        key={deployment.id}
                        className={cn(
                          "flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-2 transition-colors",
                          checked ? "border-primary/50 bg-primary/5" : "hover:bg-accent/50",
                        )}
                      >
                        <input
                          type="checkbox"
                          className="mt-1 h-4 w-4 rounded border-gray-300 accent-[#0078D4]"
                          checked={checked}
                          onChange={() => toggleDeployment(deployment.id)}
                        />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate font-mono text-xs">{deployment.id}</span>
                          <span className="block truncate text-xs text-muted-foreground">{deployment.model} · {deployment.modelVersion}</span>
                        </span>
                      </label>
                    )
                  })
                )}
              </div>
            </div>
          </section>
        </div>

        <aside className="space-y-5">
          <section className="rounded-2xl border border-slate-300/70 bg-gradient-to-br from-slate-100 to-white p-4 dark:border-slate-800 dark:from-slate-900 dark:to-slate-950">
            <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted-foreground">Override status</h3>

            <label className="mt-4 flex cursor-pointer items-start gap-3 rounded-xl border border-destructive/30 bg-destructive/5 px-3 py-3">
              <input
                type="checkbox"
                className="mt-1 h-4 w-4 rounded border-gray-300 accent-red-600"
                checked={blocked}
                onChange={(event) => setBlocked(event.target.checked)}
              />
              <span>
                <span className="block font-medium text-destructive">Block access</span>
                <span className="block text-sm text-muted-foreground">Blocked profiles deny all requests at this scope with 403 Forbidden. Plan and routing settings are ignored.</span>
              </span>
            </label>

            <label className="mt-4 flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-3">
              <input
                type="checkbox"
                className="mt-1 h-4 w-4 rounded border-gray-300 accent-[#0078D4]"
                checked={enabled}
                onChange={(event) => setEnabled(event.target.checked)}
              />
              <span>
                <span className="block font-medium">Enabled</span>
                <span className="block text-sm text-muted-foreground">Disabled overrides remain stored but no longer win in the cascade.</span>
              </span>
            </label>

            {existingProfile && (
              <div className="mt-4 rounded-xl border border-dashed px-3 py-3 text-sm text-muted-foreground">
                <p><span className="font-medium text-foreground">Profile ID</span></p>
                <p className="mt-1 break-all font-mono text-xs">{existingProfile.id}</p>
              </div>
            )}
          </section>

          {formError && (
            <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
              {formError}
            </div>
          )}

          <div className="flex flex-wrap justify-end gap-2">
            {existingProfile && onDelete && mode === "single" && (
              <Button type="button" variant="destructive" onClick={() => void onDelete()} disabled={saving || deleting}>
                {deleting ? "Deleting…" : "Delete"}
              </Button>
            )}
            <Button type="button" variant="outline" onClick={onClose} disabled={saving || deleting}>
              Cancel
            </Button>
            <Button type="button" onClick={() => void handleSave()} disabled={saving || deleting}>
              {saving ? (mode === "bulk" ? "Applying…" : "Saving…") : mode === "bulk" ? "Create overrides" : existingProfile ? "Save changes" : "Save override"}
            </Button>
          </div>
        </aside>
      </div>
    </Dialog>
  )
}

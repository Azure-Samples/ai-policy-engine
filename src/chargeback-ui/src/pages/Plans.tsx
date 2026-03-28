import { useEffect, useState, useCallback } from "react"
import { fetchPlans, createPlan, updatePlan, deletePlan, fetchDeployments } from "../api"
import type { PlanData, PlanCreateRequest, DeploymentInfo } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Button } from "../components/ui/button"
import { Input } from "../components/ui/input"
import { Badge } from "../components/ui/badge"
import { Dialog, DialogHeader, DialogTitle, DialogClose } from "../components/ui/dialog"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { Pencil, Trash2, Plus, ClipboardList } from "lucide-react"

const emptyPlanForm: PlanCreateRequest = {
  name: "",
  monthlyRate: 0,
  monthlyTokenQuota: 0,
  tokensPerMinuteLimit: 0,
  requestsPerMinuteLimit: 0,
  allowOverbilling: false,
  costPerMillionTokens: 0,
  rollUpAllDeployments: true,
  deploymentQuotas: {},
  allowedDeployments: [],
}

export function Plans() {
  const [plans, setPlans] = useState<PlanData[]>([])
  const [availableDeployments, setAvailableDeployments] = useState<DeploymentInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Plan dialog state
  const [planDialogOpen, setPlanDialogOpen] = useState(false)
  const [editingPlanId, setEditingPlanId] = useState<string | null>(null)
  const [planForm, setPlanForm] = useState<PlanCreateRequest>({ ...emptyPlanForm })
  const [saving, setSaving] = useState(false)
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null)

  // Deployment quota row input state
  const [newDeploymentId, setNewDeploymentId] = useState("")
  const [newDeploymentLimit, setNewDeploymentLimit] = useState("")

  const loadData = useCallback(async () => {
    try {
      const [plansRes, deploymentsRes] = await Promise.all([
        fetchPlans(),
        fetchDeployments().catch(() => ({ deployments: [] })),
      ])
      setPlans(plansRes.plans ?? [])
      setAvailableDeployments(deploymentsRes.deployments ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load data")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { loadData() }, [loadData])

  const openCreatePlan = () => {
    setEditingPlanId(null)
    setPlanForm({ ...emptyPlanForm })
    setNewDeploymentId("")
    setNewDeploymentLimit("")
    setPlanDialogOpen(true)
  }

  const openEditPlan = (p: PlanData) => {
    setEditingPlanId(p.id)
    setPlanForm({
      name: p.name,
      monthlyRate: p.monthlyRate,
      monthlyTokenQuota: p.monthlyTokenQuota,
      tokensPerMinuteLimit: p.tokensPerMinuteLimit,
      requestsPerMinuteLimit: p.requestsPerMinuteLimit,
      allowOverbilling: p.allowOverbilling,
      costPerMillionTokens: p.costPerMillionTokens,
      rollUpAllDeployments: p.rollUpAllDeployments ?? true,
      deploymentQuotas: p.deploymentQuotas ?? {},
      allowedDeployments: p.allowedDeployments ?? [],
    })
    setNewDeploymentId("")
    setNewDeploymentLimit("")
    setPlanDialogOpen(true)
  }

  const buildPlanPayload = (): PlanCreateRequest => {
    const rollUpAllDeployments = planForm.rollUpAllDeployments !== false
    const deploymentQuotas = { ...(planForm.deploymentQuotas ?? {}) }
    const pendingDeploymentId = newDeploymentId.trim()

    if (!rollUpAllDeployments && pendingDeploymentId) {
      deploymentQuotas[pendingDeploymentId] = parseInt(newDeploymentLimit, 10) || 0
    }

    return {
      ...planForm,
      name: planForm.name.trim(),
      monthlyRate: Number(planForm.monthlyRate) || 0,
      monthlyTokenQuota: rollUpAllDeployments ? (Number(planForm.monthlyTokenQuota) || 0) : 0,
      tokensPerMinuteLimit: Number(planForm.tokensPerMinuteLimit) || 0,
      requestsPerMinuteLimit: Number(planForm.requestsPerMinuteLimit) || 0,
      costPerMillionTokens: Number(planForm.costPerMillionTokens) || 0,
      rollUpAllDeployments,
      deploymentQuotas: rollUpAllDeployments ? {} : deploymentQuotas,
      allowedDeployments: planForm.allowedDeployments ?? [],
    }
  }

  const isPlanFormValid = (): boolean => {
    if (!planForm.name.trim()) return false
    if (Number(planForm.monthlyRate) < 0) return false
    if (Number(planForm.tokensPerMinuteLimit) < 0) return false
    if (Number(planForm.requestsPerMinuteLimit) < 0) return false
    if (Number(planForm.costPerMillionTokens) < 0) return false
    if (planForm.rollUpAllDeployments !== false && Number(planForm.monthlyTokenQuota) < 0) return false
    return true
  }

  const handleSavePlan = async () => {
    setSaving(true)
    try {
      const payload = buildPlanPayload()
      if (editingPlanId) {
        await updatePlan(editingPlanId, payload)
      } else {
        await createPlan(payload)
      }
      setPlanDialogOpen(false)
      setNewDeploymentId("")
      setNewDeploymentLimit("")
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save plan")
    } finally {
      setSaving(false)
    }
  }

  const handleDeletePlan = async (planId: string) => {
    try {
      await deletePlan(planId)
      setDeleteConfirm(null)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete plan")
    }
  }

  const updateField = (field: keyof PlanCreateRequest, value: string | boolean) => {
    setPlanForm((prev) => ({ ...prev, [field]: value }))
  }

  if (error && plans.length === 0) {
    return (
      <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-destructive">
        Error: {error}
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <ClipboardList className="h-6 w-6 text-[#0078D4]" />
        <h2 className="text-xl font-bold">Plan Management</h2>
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-destructive text-sm">
          {error}
          <Button variant="ghost" size="sm" className="ml-2" onClick={() => setError(null)}>Dismiss</Button>
        </div>
      )}

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <CardTitle className="text-base">Billing Plans</CardTitle>
          <Button size="sm" className="gap-1" onClick={openCreatePlan}>
            <Plus className="h-4 w-4" /> Create Plan
          </Button>
        </CardHeader>
        <CardContent>
          {loading ? (
            <div className="text-center text-muted-foreground py-8">Loading plans…</div>
          ) : plans.length === 0 ? (
            <div className="text-center text-muted-foreground py-8">No plans created yet.</div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Monthly Rate ($)</TableHead>
                  <TableHead>Token Quota</TableHead>
                  <TableHead>Quota Mode</TableHead>
                  <TableHead>TPM Limit</TableHead>
                  <TableHead>RPM Limit</TableHead>
                  <TableHead>Overbilling</TableHead>
                  <TableHead>Cost/M Tokens</TableHead>
                  <TableHead>Allowed Deployments</TableHead>
                  <TableHead className="w-[100px]">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {plans.map((p) => (
                  <TableRow key={p.id}>
                    <TableCell className="font-medium">{p.name}</TableCell>
                    <TableCell className="font-mono">${p.monthlyRate.toFixed(2)}</TableCell>
                    <TableCell className="font-mono">
                      {p.rollUpAllDeployments !== false ? p.monthlyTokenQuota.toLocaleString() : "—"}
                    </TableCell>
                    <TableCell>
                      <Badge variant={p.rollUpAllDeployments !== false ? "blue" : "green"}>
                        {p.rollUpAllDeployments !== false ? "Roll-up" : "Per-deployment"}
                      </Badge>
                    </TableCell>
                    <TableCell className="font-mono">{p.tokensPerMinuteLimit.toLocaleString()}</TableCell>
                    <TableCell className="font-mono">{p.requestsPerMinuteLimit.toLocaleString()}</TableCell>
                    <TableCell>
                      <Badge variant={p.allowOverbilling ? "green" : "red"}>
                        {p.allowOverbilling ? "Yes" : "No"}
                      </Badge>
                    </TableCell>
                    <TableCell className="font-mono">${p.costPerMillionTokens.toFixed(2)}</TableCell>
                    <TableCell>
                      {!p.allowedDeployments || p.allowedDeployments.length === 0 ? (
                        <Badge variant="blue">All</Badge>
                      ) : (
                        <span title={p.allowedDeployments.join(", ")}>
                          <Badge variant="green">{p.allowedDeployments.length} selected</Badge>
                        </span>
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex gap-1">
                        <Button variant="ghost" size="icon" onClick={() => openEditPlan(p)}>
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => setDeleteConfirm(p.id)}>
                          <Trash2 className="h-4 w-4 text-[#D13438]" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {/* Plan Create/Edit Dialog */}
      <Dialog open={planDialogOpen} onOpenChange={(open) => !open && setPlanDialogOpen(false)}>
        <DialogClose onClose={() => setPlanDialogOpen(false)} />
        <DialogHeader>
          <DialogTitle>{editingPlanId ? "Edit Plan" : "Create Plan"}</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <div className="space-y-2">
            <label className="text-sm font-medium">Name</label>
            <Input value={planForm.name} onChange={(e) => updateField("name", e.target.value)} placeholder="Plan name" />
            {planForm.name.length > 0 && !planForm.name.trim() && (
              <p className="text-xs text-destructive">Plan name cannot be only whitespace.</p>
            )}
          </div>
          <div className={`grid gap-4 ${planForm.rollUpAllDeployments !== false ? "grid-cols-2" : "grid-cols-1"}`}>
            <div className="space-y-2">
              <label className="text-sm font-medium">Monthly Rate ($)</label>
              <Input type="number" step="0.01" min="0" value={planForm.monthlyRate} onChange={(e) => updateField("monthlyRate", e.target.value)} />
            </div>
            {planForm.rollUpAllDeployments !== false && (
              <div className="space-y-2">
                <label className="text-sm font-medium">Monthly Token Quota</label>
                <Input type="number" min="0" value={planForm.monthlyTokenQuota} onChange={(e) => updateField("monthlyTokenQuota", e.target.value)} />
              </div>
            )}
          </div>
          {planForm.rollUpAllDeployments === false && (
            <p className="text-xs text-muted-foreground">
              Overall monthly token quota is disabled in per-deployment mode.
            </p>
          )}
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <label className="text-sm font-medium">Tokens/Min Limit</label>
              <Input type="number" min="0" value={planForm.tokensPerMinuteLimit} onChange={(e) => updateField("tokensPerMinuteLimit", e.target.value)} />
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium">Requests/Min Limit</label>
              <Input type="number" min="0" value={planForm.requestsPerMinuteLimit} onChange={(e) => updateField("requestsPerMinuteLimit", e.target.value)} />
            </div>
          </div>
          <div className="space-y-2">
            <label className="text-sm font-medium">Cost per Million Tokens ($)</label>
            <Input type="number" step="0.01" min="0" value={planForm.costPerMillionTokens} onChange={(e) => updateField("costPerMillionTokens", e.target.value)} />
          </div>
          <div className="flex items-center gap-2">
            <input
              type="checkbox"
              id="allowOverbilling"
              checked={planForm.allowOverbilling}
              onChange={(e) => updateField("allowOverbilling", e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 accent-[#0078D4]"
            />
            <label htmlFor="allowOverbilling" className="text-sm font-medium">Allow Overbilling</label>
          </div>
          <div className="flex items-center gap-2">
            <input
              type="checkbox"
              id="rollUpAllDeployments"
              checked={planForm.rollUpAllDeployments !== false}
              onChange={(e) => {
                setPlanForm((prev) => ({ ...prev, rollUpAllDeployments: e.target.checked }))
              }}
              className="h-4 w-4 rounded border-gray-300 accent-[#0078D4]"
            />
            <label htmlFor="rollUpAllDeployments" className="text-sm font-medium">Roll up all deployments</label>
          </div>
          {planForm.rollUpAllDeployments === false && (
            <div className="space-y-2 rounded border p-3">
              <label className="text-sm font-medium block">Per-Deployment Quotas</label>
              {Object.entries(planForm.deploymentQuotas ?? {}).map(([depId, limit]) => (
                <div key={depId} className="flex items-center gap-2">
                  <span className="text-sm font-mono flex-1">{depId}</span>
                  <Input
                    type="number"
                    className="w-40"
                    value={limit}
                    onChange={(e) => {
                      const val = parseInt(e.target.value, 10) || 0
                      setPlanForm((prev) => ({
                        ...prev,
                        deploymentQuotas: { ...prev.deploymentQuotas, [depId]: val },
                      }))
                    }}
                  />
                  <Button
                    variant="ghost"
                    size="icon"
                    onClick={() => {
                      setPlanForm((prev) => {
                        const next = { ...prev.deploymentQuotas }
                        delete next[depId]
                        return { ...prev, deploymentQuotas: next }
                      })
                    }}
                  >
                    <Trash2 className="h-4 w-4 text-[#D13438]" />
                  </Button>
                </div>
              ))}
              <div className="flex items-center gap-2">
                <Input
                  placeholder="Deployment ID (e.g. gpt-4.1)"
                  value={newDeploymentId}
                  onChange={(e) => setNewDeploymentId(e.target.value)}
                  className="flex-1"
                />
                <Input
                  type="number"
                  placeholder="Token limit"
                  value={newDeploymentLimit}
                  onChange={(e) => setNewDeploymentLimit(e.target.value)}
                  className="w-40"
                />
                <Button
                  size="sm"
                  variant="outline"
                  disabled={!newDeploymentId.trim()}
                  onClick={() => {
                    const deploymentId = newDeploymentId.trim()
                    if (!deploymentId) return
                    const limit = parseInt(newDeploymentLimit, 10) || 0
                    setPlanForm((prev) => ({
                      ...prev,
                      deploymentQuotas: { ...prev.deploymentQuotas, [deploymentId]: limit },
                    }))
                    setNewDeploymentId("")
                    setNewDeploymentLimit("")
                  }}
                >
                  <Plus className="h-4 w-4" />
                </Button>
              </div>
            </div>
          )}
          <div className="space-y-2 rounded border p-3">
            <label className="text-sm font-medium block">Allowed Deployments</label>
            <p className="text-xs text-muted-foreground">
              Empty = all deployments allowed. Select specific deployments to restrict access.
            </p>
            {availableDeployments.length === 0 ? (
              <p className="text-xs text-muted-foreground italic">
                No deployments found — configure AZURE_AI_ENDPOINT to enable deployment discovery.
              </p>
            ) : (
              <div className="space-y-1 max-h-48 overflow-y-auto">
                {availableDeployments.map((dep) => {
                  const checked = (planForm.allowedDeployments ?? []).includes(dep.id)
                  return (
                    <div key={dep.id} className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        id={`plan-dep-${dep.id}`}
                        checked={checked}
                        onChange={(e) => {
                          setPlanForm((prev) => {
                            const current = prev.allowedDeployments ?? []
                            const next = e.target.checked
                              ? [...current, dep.id]
                              : current.filter((d) => d !== dep.id)
                            return { ...prev, allowedDeployments: next }
                          })
                        }}
                        className="h-4 w-4 rounded border-gray-300 accent-[#0078D4]"
                      />
                      <label htmlFor={`plan-dep-${dep.id}`} className="text-sm">
                        <span className="font-mono">{dep.id}</span>
                        <span className="text-muted-foreground ml-1">({dep.model})</span>
                      </label>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setPlanDialogOpen(false)}>Cancel</Button>
            <Button onClick={handleSavePlan} disabled={saving || !isPlanFormValid()}>
              {saving ? "Saving…" : editingPlanId ? "Update" : "Create"}
            </Button>
          </div>
        </div>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={!!deleteConfirm} onOpenChange={(open) => !open && setDeleteConfirm(null)}>
        <DialogClose onClose={() => setDeleteConfirm(null)} />
        <DialogHeader>
          <DialogTitle>Confirm Delete</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete this plan? This action cannot be undone.
          </p>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setDeleteConfirm(null)}>Cancel</Button>
            <Button
              variant="destructive"
              onClick={() => {
                if (deleteConfirm) handleDeletePlan(deleteConfirm)
              }}
            >
              Delete
            </Button>
          </div>
        </div>
      </Dialog>
    </div>
  )
}

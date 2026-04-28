import { useEffect, useState, useCallback } from "react"
import { fetchRoutingPolicies, createRoutingPolicy, updateRoutingPolicy, deleteRoutingPolicy, fetchDeployments, fetchPlans } from "../api"
import type { ModelRoutingPolicy, ModelRoutingPolicyCreateRequest, RouteRule, RoutingBehavior, DeploymentInfo, PlanData } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Button } from "../components/ui/button"
import { Input } from "../components/ui/input"
import { Badge } from "../components/ui/badge"
import { Dialog, DialogHeader, DialogTitle, DialogClose } from "../components/ui/dialog"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { Pencil, Trash2, Plus, Route, ArrowRight, AlertTriangle } from "lucide-react"

interface PolicyForm {
  name: string
  description: string
  defaultBehavior: RoutingBehavior
  fallbackDeployment: string
  rules: RouteRule[]
}

const emptyForm: PolicyForm = {
  name: "",
  description: "",
  defaultBehavior: "Passthrough",
  fallbackDeployment: "",
  rules: [],
}

export function RoutingPolicies() {
  const [policies, setPolicies] = useState<ModelRoutingPolicy[]>([])
  const [deployments, setDeployments] = useState<DeploymentInfo[]>([])
  const [plans, setPlans] = useState<PlanData[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [form, setForm] = useState<PolicyForm>({ ...emptyForm })
  const [saving, setSaving] = useState(false)
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null)

  // Rule builder state
  const [newRuleRequested, setNewRuleRequested] = useState("")
  const [newRuleRouted, setNewRuleRouted] = useState("")

  const loadData = useCallback(async () => {
    try {
      const [policiesRes, deploymentsRes, plansRes] = await Promise.all([
        fetchRoutingPolicies(),
        fetchDeployments().catch(() => ({ deployments: [] })),
        fetchPlans().catch(() => ({ plans: [] })),
      ])
      setPolicies(policiesRes.policies ?? [])
      setDeployments(deploymentsRes.deployments ?? [])
      setPlans(plansRes.plans ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load data")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { loadData() }, [loadData])

  const policyInUseBy = (policyId: string): string[] => {
    return plans
      .filter(p => p.modelRoutingPolicyId === policyId)
      .map(p => p.name)
  }

  const openCreate = () => {
    setEditingId(null)
    setForm({ ...emptyForm })
    setNewRuleRequested("")
    setNewRuleRouted("")
    setDialogOpen(true)
  }

  const openEdit = (p: ModelRoutingPolicy) => {
    setEditingId(p.id)
    setForm({
      name: p.name,
      description: p.description ?? "",
      defaultBehavior: p.defaultBehavior,
      fallbackDeployment: p.fallbackDeployment ?? "",
      rules: [...p.rules],
    })
    setNewRuleRequested("")
    setNewRuleRouted("")
    setDialogOpen(true)
  }

  const handleSave = async () => {
    setSaving(true)
    try {
      const payload: ModelRoutingPolicyCreateRequest = {
        name: form.name.trim(),
        description: form.description.trim() || undefined,
        defaultBehavior: form.defaultBehavior,
        fallbackDeployment: form.fallbackDeployment.trim() || undefined,
        rules: form.rules,
      }
      if (editingId) {
        await updateRoutingPolicy(editingId, payload)
      } else {
        await createRoutingPolicy(payload)
      }
      setDialogOpen(false)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save routing policy")
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async (policyId: string) => {
    try {
      await deleteRoutingPolicy(policyId)
      setDeleteConfirm(null)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete routing policy")
    }
  }

  const addRule = () => {
    const requested = newRuleRequested.trim()
    const routed = newRuleRouted.trim()
    if (!requested || !routed) return
    setForm(prev => ({
      ...prev,
      rules: [...prev.rules, { requestedDeployment: requested, routedDeployment: routed, priority: prev.rules.length + 1, enabled: true }],
    }))
    setNewRuleRequested("")
    setNewRuleRouted("")
  }

  const removeRule = (index: number) => {
    setForm(prev => ({
      ...prev,
      rules: prev.rules.filter((_, i) => i !== index),
    }))
  }

  const isFormValid = form.name.trim().length > 0

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <Route className="h-8 w-8 text-[#0078D4] animate-pulse" />
        <span className="ml-2 text-muted-foreground">Loading routing policies…</span>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Route className="h-7 w-7 text-[#0078D4]" />
          <div>
            <h2 className="text-2xl font-bold tracking-tight">Routing Policies</h2>
            <p className="text-sm text-muted-foreground">
              Configure auto-routing rules that map deployment requests to actual Foundry deployments.
            </p>
          </div>
        </div>
        <Button onClick={openCreate} className="gap-2">
          <Plus className="h-4 w-4" />
          Create Policy
        </Button>
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive flex items-center gap-2">
          <AlertTriangle className="h-4 w-4" />
          {error}
          <Button variant="ghost" size="sm" className="ml-auto" onClick={() => setError(null)}>Dismiss</Button>
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">
            Configured Policies
            <Badge variant="secondary" className="ml-2">{policies.length}</Badge>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {policies.length === 0 ? (
            <div className="text-center py-12 text-muted-foreground">
              <Route className="h-10 w-10 mx-auto mb-3 opacity-40" />
              <p>No routing policies configured yet.</p>
              <p className="text-sm mt-1">Click "Create Policy" to define auto-routing rules.</p>
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead>Default Behavior</TableHead>
                  <TableHead>Fallback</TableHead>
                  <TableHead>Rules</TableHead>
                  <TableHead>Used By</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {policies.map(p => {
                  const usedBy = policyInUseBy(p.id)
                  return (
                    <TableRow key={p.id}>
                      <TableCell className="font-medium">{p.name}</TableCell>
                      <TableCell className="text-muted-foreground text-sm max-w-[200px] truncate">
                        {p.description || "—"}
                      </TableCell>
                      <TableCell>
                        <Badge variant={p.defaultBehavior === "Passthrough" ? "blue" : "red"}>
                          {p.defaultBehavior}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        {p.fallbackDeployment ? (
                          <code className="rounded bg-muted px-2 py-1 text-xs font-mono">{p.fallbackDeployment}</code>
                        ) : (
                          <span className="text-muted-foreground">—</span>
                        )}
                      </TableCell>
                      <TableCell>
                        <Badge variant="secondary">{p.rules.length} rule{p.rules.length !== 1 ? "s" : ""}</Badge>
                      </TableCell>
                      <TableCell>
                        {usedBy.length > 0 ? (
                          <div className="flex flex-wrap gap-1">
                            {usedBy.map(name => (
                              <Badge key={name} variant="green">{name}</Badge>
                            ))}
                          </div>
                        ) : (
                          <span className="text-muted-foreground text-xs">None</span>
                        )}
                      </TableCell>
                      <TableCell className="text-right">
                        <div className="flex items-center justify-end gap-1">
                          <Button variant="ghost" size="icon" onClick={() => openEdit(p)} title="Edit">
                            <Pencil className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" onClick={() => setDeleteConfirm(p.id)} title="Delete" className="text-destructive hover:text-destructive">
                            <Trash2 className="h-4 w-4" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {/* Create/Edit Dialog */}
      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogClose onClose={() => setDialogOpen(false)} />
        <DialogHeader>
          <DialogTitle>{editingId ? "Edit Routing Policy" : "Create Routing Policy"}</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <div>
            <label className="text-sm font-medium mb-1 block">Policy Name</label>
            <Input
              value={form.name}
              onChange={e => setForm(prev => ({ ...prev, name: e.target.value }))}
              placeholder="e.g. Production Auto-Router"
            />
          </div>
          <div>
            <label className="text-sm font-medium mb-1 block">Description</label>
            <Input
              value={form.description}
              onChange={e => setForm(prev => ({ ...prev, description: e.target.value }))}
              placeholder="Optional description"
            />
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="text-sm font-medium mb-1 block">Default Behavior</label>
              <select
                className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                value={form.defaultBehavior}
                onChange={e => setForm(prev => ({ ...prev, defaultBehavior: e.target.value as RoutingBehavior }))}
              >
                <option value="Passthrough">Passthrough — allow unmatched requests</option>
                <option value="Deny">Deny — block unmatched requests</option>
              </select>
            </div>
            <div>
              <label className="text-sm font-medium mb-1 block">Fallback Deployment</label>
              {deployments.length > 0 ? (
                <select
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
                  value={form.fallbackDeployment}
                  onChange={e => setForm(prev => ({ ...prev, fallbackDeployment: e.target.value }))}
                >
                  <option value="">None</option>
                  {deployments.map(d => (
                    <option key={d.id} value={d.id}>{d.id} ({d.model})</option>
                  ))}
                </select>
              ) : (
                <Input
                  value={form.fallbackDeployment}
                  onChange={e => setForm(prev => ({ ...prev, fallbackDeployment: e.target.value }))}
                  placeholder="Deployment ID"
                  className="font-mono"
                />
              )}
            </div>
          </div>

          {/* Route Rules Builder */}
          <div className="space-y-3 rounded border p-3">
            <label className="text-sm font-medium block">Route Rules</label>
            <p className="text-xs text-muted-foreground">
              Map requested deployments to actual Foundry deployments. When no deployment is specified in a request, the auto-router uses the fallback.
            </p>
            {form.rules.length > 0 && (
              <div className="space-y-2">
                {form.rules.map((rule, i) => (
                  <div key={i} className="flex items-center gap-2 text-sm">
                    <span className="text-xs text-muted-foreground w-6 text-right shrink-0">#{rule.priority}</span>
                    <label className="flex items-center gap-1 shrink-0" title={rule.enabled ? "Enabled" : "Disabled"}>
                      <input
                        type="checkbox"
                        checked={rule.enabled}
                        onChange={e => {
                          setForm(prev => ({
                            ...prev,
                            rules: prev.rules.map((r, idx) => idx === i ? { ...r, enabled: e.target.checked } : r),
                          }))
                        }}
                        className="rounded border-input"
                      />
                    </label>
                    <code className={`rounded bg-muted px-2 py-1 text-xs font-mono flex-1 truncate ${!rule.enabled ? 'opacity-50' : ''}`}>{rule.requestedDeployment}</code>
                    <ArrowRight className={`h-4 w-4 text-muted-foreground shrink-0 ${!rule.enabled ? 'opacity-50' : ''}`} />
                    <code className={`rounded bg-muted px-2 py-1 text-xs font-mono flex-1 truncate ${!rule.enabled ? 'opacity-50' : ''}`}>{rule.routedDeployment}</code>
                    <Input
                      type="number"
                      min="1"
                      value={rule.priority}
                      onChange={e => {
                        const priority = parseInt(e.target.value, 10) || 1
                        setForm(prev => ({
                          ...prev,
                          rules: prev.rules.map((r, idx) => idx === i ? { ...r, priority } : r),
                        }))
                      }}
                      className="w-16 h-8 text-xs font-mono shrink-0"
                      title="Priority"
                    />
                    <Button variant="ghost" size="icon" className="shrink-0" onClick={() => removeRule(i)}>
                      <Trash2 className="h-4 w-4 text-[#D13438]" />
                    </Button>
                  </div>
                ))}
              </div>
            )}
            <div className="flex items-center gap-2">
              {deployments.length > 0 ? (
                <>
                  <select
                    className="flex h-10 flex-1 rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
                    value={newRuleRequested}
                    onChange={e => setNewRuleRequested(e.target.value)}
                  >
                    <option value="">Requested…</option>
                    {deployments.map(d => (
                      <option key={d.id} value={d.id}>{d.id}</option>
                    ))}
                  </select>
                  <ArrowRight className="h-4 w-4 text-muted-foreground shrink-0" />
                  <select
                    className="flex h-10 flex-1 rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
                    value={newRuleRouted}
                    onChange={e => setNewRuleRouted(e.target.value)}
                  >
                    <option value="">Routed to…</option>
                    {deployments.map(d => (
                      <option key={d.id} value={d.id}>{d.id}</option>
                    ))}
                  </select>
                </>
              ) : (
                <>
                  <Input
                    placeholder="Requested deployment"
                    value={newRuleRequested}
                    onChange={e => setNewRuleRequested(e.target.value)}
                    className="flex-1 font-mono"
                  />
                  <ArrowRight className="h-4 w-4 text-muted-foreground shrink-0" />
                  <Input
                    placeholder="Routed deployment"
                    value={newRuleRouted}
                    onChange={e => setNewRuleRouted(e.target.value)}
                    className="flex-1 font-mono"
                  />
                </>
              )}
              <Button
                size="sm"
                variant="outline"
                disabled={!newRuleRequested.trim() || !newRuleRouted.trim()}
                onClick={addRule}
                className="shrink-0"
              >
                <Plus className="h-4 w-4" />
              </Button>
            </div>
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setDialogOpen(false)}>Cancel</Button>
            <Button onClick={handleSave} disabled={saving || !isFormValid}>
              {saving ? "Saving…" : editingId ? "Update" : "Create"}
            </Button>
          </div>
        </div>
      </Dialog>

      {/* Delete Confirmation */}
      <Dialog open={!!deleteConfirm} onOpenChange={() => setDeleteConfirm(null)}>
        <DialogClose onClose={() => setDeleteConfirm(null)} />
        <DialogHeader>
          <DialogTitle>Delete Routing Policy</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          {deleteConfirm && policyInUseBy(deleteConfirm).length > 0 && (
            <div className="flex items-start gap-2 rounded-md bg-amber-50 dark:bg-amber-950/30 p-3 text-amber-800 dark:text-amber-200 text-sm">
              <AlertTriangle className="h-4 w-4 mt-0.5 shrink-0" />
              <span>
                This policy is currently used by: <strong>{policyInUseBy(deleteConfirm).join(", ")}</strong>.
                Deleting it will remove the routing configuration from those plans.
              </span>
            </div>
          )}
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete this routing policy? This action cannot be undone.
          </p>
          <div className="flex justify-end gap-2">
            <Button variant="outline" onClick={() => setDeleteConfirm(null)}>Cancel</Button>
            <Button variant="destructive" onClick={() => deleteConfirm && handleDelete(deleteConfirm)}>
              Delete
            </Button>
          </div>
        </div>
      </Dialog>
    </div>
  )
}

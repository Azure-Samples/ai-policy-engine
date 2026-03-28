import { useEffect, useState, useCallback } from "react"
import { fetchPricing, updatePricing, deletePricing } from "../api"
import type { ModelPricing, ModelPricingCreateRequest } from "../types"
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card"
import { Button } from "../components/ui/button"
import { Input } from "../components/ui/input"
import { Badge } from "../components/ui/badge"
import { Dialog, DialogHeader, DialogTitle, DialogClose } from "../components/ui/dialog"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../components/ui/table"
import { DollarSign, Pencil, Trash2, Plus, AlertTriangle } from "lucide-react"

const emptyForm: ModelPricingCreateRequest = {
  modelId: "",
  promptRatePer1K: 0,
  completionRatePer1K: 0,
  imageRatePer1K: 0,
}

function formatRate(value: number): string {
  return `$${value.toFixed(4)}`
}

export function Pricing() {
  const [models, setModels] = useState<ModelPricing[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingModelId, setEditingModelId] = useState<string | null>(null)
  const [form, setForm] = useState<ModelPricingCreateRequest>({ ...emptyForm })
  const [displayName, setDisplayName] = useState("")
  const [saving, setSaving] = useState(false)
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null)

  const loadData = useCallback(async () => {
    try {
      const res = await fetchPricing()
      setModels(res.models ?? [])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load pricing data")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { loadData() }, [loadData])

  const openCreate = () => {
    setEditingModelId(null)
    setForm({ ...emptyForm })
    setDisplayName("")
    setDialogOpen(true)
  }

  const openEdit = (m: ModelPricing) => {
    setEditingModelId(m.modelId)
    setForm({
      modelId: m.modelId,
      promptRatePer1K: m.promptRatePer1K,
      completionRatePer1K: m.completionRatePer1K,
      imageRatePer1K: m.imageRatePer1K,
    })
    setDisplayName(m.displayName ?? "")
    setDialogOpen(true)
  }

  const handleSave = async () => {
    setSaving(true)
    try {
      const payload: ModelPricingCreateRequest = {
        ...form,
        displayName: displayName || undefined,
      }
      const modelId = editingModelId ?? form.modelId
      await updatePricing(modelId, payload)
      setDialogOpen(false)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save pricing")
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async (modelId: string) => {
    try {
      await deletePricing(modelId)
      setDeleteConfirm(null)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete pricing")
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <DollarSign className="h-8 w-8 text-[#0078D4] animate-pulse" />
        <span className="ml-2 text-muted-foreground">Loading pricing data…</span>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <DollarSign className="h-7 w-7 text-[#0078D4]" />
          <div>
            <h2 className="text-2xl font-bold tracking-tight">Model Pricing</h2>
            <p className="text-sm text-muted-foreground">
              Configure per-model token rates. These rates determine the "Cost to Us" calculation in the dashboard.
            </p>
          </div>
        </div>
        <Button onClick={openCreate} className="gap-2">
          <Plus className="h-4 w-4" />
          Add Model
        </Button>
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive flex items-center gap-2">
          <AlertTriangle className="h-4 w-4" />
          {error}
        </div>
      )}

      {/* Pricing Table */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">
            Configured Models
            <Badge variant="secondary" className="ml-2">{models.length}</Badge>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {models.length === 0 ? (
            <div className="text-center py-12 text-muted-foreground">
              <DollarSign className="h-10 w-10 mx-auto mb-3 opacity-40" />
              <p>No model pricing configured yet.</p>
              <p className="text-sm mt-1">Click "Add Model" to define token rates for a model.</p>
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Model ID</TableHead>
                  <TableHead>Display Name</TableHead>
                  <TableHead className="text-right">Prompt Rate</TableHead>
                  <TableHead className="text-right">Completion Rate</TableHead>
                  <TableHead className="text-right">Image Rate</TableHead>
                  <TableHead>Last Updated</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {models.map((m) => (
                  <TableRow key={m.modelId}>
                    <TableCell>
                      <code className="rounded bg-muted px-2 py-1 text-xs font-mono">{m.modelId}</code>
                    </TableCell>
                    <TableCell>{m.displayName || <span className="text-muted-foreground italic">—</span>}</TableCell>
                    <TableCell className="text-right font-mono text-sm">
                      {formatRate(m.promptRatePer1K)} <span className="text-muted-foreground text-xs">/ 1K</span>
                    </TableCell>
                    <TableCell className="text-right font-mono text-sm">
                      {formatRate(m.completionRatePer1K)} <span className="text-muted-foreground text-xs">/ 1K</span>
                    </TableCell>
                    <TableCell className="text-right font-mono text-sm">
                      {m.imageRatePer1K > 0 ? (
                        <>{formatRate(m.imageRatePer1K)} <span className="text-muted-foreground text-xs">/ 1K</span></>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {m.updatedAt ? new Date(m.updatedAt).toLocaleDateString() : "—"}
                    </TableCell>
                    <TableCell className="text-right">
                      <div className="flex items-center justify-end gap-1">
                        <Button variant="ghost" size="icon" onClick={() => openEdit(m)} title="Edit">
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => setDeleteConfirm(m.modelId)} title="Delete" className="text-destructive hover:text-destructive">
                          <Trash2 className="h-4 w-4" />
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

      {/* Create / Edit Dialog */}
      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogClose onClose={() => setDialogOpen(false)} />
        <DialogHeader>
          <DialogTitle>{editingModelId ? "Edit Model Pricing" : "Add Model Pricing"}</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <div>
            <label className="text-sm font-medium mb-1 block">Model ID</label>
            <Input
              value={form.modelId}
              onChange={(e) => setForm({ ...form, modelId: e.target.value })}
              placeholder="e.g. gpt-4.1"
              disabled={!!editingModelId}
              className="font-mono"
            />
            {!!editingModelId && (
              <p className="text-xs text-muted-foreground mt-1">Model ID cannot be changed after creation.</p>
            )}
          </div>
          <div>
            <label className="text-sm font-medium mb-1 block">Display Name</label>
            <Input
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="e.g. GPT-4o"
            />
          </div>
          <div className="grid grid-cols-3 gap-4">
            <div>
              <label className="text-sm font-medium mb-1 block">Prompt Rate / 1K tokens</label>
              <Input
                type="number"
                step="0.0001"
                min="0"
                value={form.promptRatePer1K}
                onChange={(e) => setForm({ ...form, promptRatePer1K: parseFloat(e.target.value) || 0 })}
              />
            </div>
            <div>
              <label className="text-sm font-medium mb-1 block">Completion Rate / 1K tokens</label>
              <Input
                type="number"
                step="0.0001"
                min="0"
                value={form.completionRatePer1K}
                onChange={(e) => setForm({ ...form, completionRatePer1K: parseFloat(e.target.value) || 0 })}
              />
            </div>
            <div>
              <label className="text-sm font-medium mb-1 block">Image Rate / 1K tokens</label>
              <Input
                type="number"
                step="0.0001"
                min="0"
                value={form.imageRatePer1K}
                onChange={(e) => setForm({ ...form, imageRatePer1K: parseFloat(e.target.value) || 0 })}
              />
            </div>
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setDialogOpen(false)}>Cancel</Button>
            <Button onClick={handleSave} disabled={saving || (!editingModelId && !form.modelId)}>
              {saving ? "Saving…" : "Save"}
            </Button>
          </div>
        </div>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={!!deleteConfirm} onOpenChange={() => setDeleteConfirm(null)}>
        <DialogClose onClose={() => setDeleteConfirm(null)} />
        <DialogHeader>
          <DialogTitle>Delete Model Pricing</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete pricing for <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs">{deleteConfirm}</code>? This action cannot be undone.
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

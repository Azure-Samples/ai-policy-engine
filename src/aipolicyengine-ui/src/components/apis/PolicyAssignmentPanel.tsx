import { useEffect, useMemo, useState } from "react"
import { AlertTriangle, CheckCircle2, Code2, LoaderCircle, RefreshCcw, ShieldCheck, Trash2, Wand2 } from "lucide-react"
import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "../ui/card"
import { Dialog, DialogClose, DialogHeader, DialogTitle } from "../ui/dialog"
import type { ApimTemplateSummary, PolicyAssignment, PolicyAssignmentStatus, PolicyDocumentResponse } from "../../types/apim"

interface SelectedTargetSummary {
  key: string
  kind: "api" | "operation"
  title: string
  subtitle: string
}

interface PolicyAssignmentPanelProps {
  selectedTarget: SelectedTargetSummary | null
  policyDocument: PolicyDocumentResponse | null
  policyLoading: boolean
  policyError: string | null
  templates: ApimTemplateSummary[]
  busy: boolean
  onAssign: () => void
  onClear: () => Promise<void>
  onRetry: () => void
}

function formatDate(value?: string | null): string {
  if (!value) return "—"
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

function getStatusPresentation(status?: PolicyAssignmentStatus) {
  switch (status) {
    case "synced":
      return { label: "Synced", variant: "green" as const, icon: CheckCircle2 }
    case "pending":
      return { label: "Pending", variant: "blue" as const, icon: LoaderCircle, spinning: true }
    case "applying":
      return { label: "Applying", variant: "blue" as const, icon: LoaderCircle, spinning: true }
    case "failed":
      return { label: "Failed", variant: "red" as const, icon: AlertTriangle }
    default:
      return { label: "Unassigned", variant: "secondary" as const, icon: ShieldCheck }
  }
}

function statusDetail(assignment: PolicyAssignment | null): string | null {
  if (!assignment) return null
  return assignment.errorMessage
}

export function PolicyAssignmentPanel({
  selectedTarget,
  policyDocument,
  policyLoading,
  policyError,
  templates,
  busy,
  onAssign,
  onClear,
  onRetry,
}: PolicyAssignmentPanelProps) {
  const [showXml, setShowXml] = useState(false)
  const [confirmClearOpen, setConfirmClearOpen] = useState(false)

  useEffect(() => {
    const timeoutId = window.setTimeout(() => {
      setShowXml(false)
      setConfirmClearOpen(false)
    }, 0)

    return () => {
      window.clearTimeout(timeoutId)
    }
  }, [selectedTarget?.key])

  const assignment = policyDocument?.assignment ?? null
  const assignmentStatus = getStatusPresentation(assignment?.status)
  const templateMap = useMemo(
    () => Object.fromEntries(templates.map((template) => [template.id, template])),
    [templates],
  )
  const resolvedTemplate = assignment?.templateId ? templateMap[assignment.templateId] : undefined
  const resolvedTemplateName = resolvedTemplate?.displayName ?? assignment?.templateId ?? "None"
  const resolvedTemplateVersion = assignment?.templateVersion ?? resolvedTemplate?.version ?? "—"
  const detail = statusDetail(assignment)

  if (!selectedTarget) {
    return (
      <Card className="h-full">
        <CardContent className="flex h-full min-h-[420px] flex-col items-center justify-center gap-3 text-center text-muted-foreground">
          <ShieldCheck className="h-10 w-10 text-[#0078D4]" />
          <div>
            <p className="font-medium text-foreground">Select an API or operation</p>
            <p className="text-sm">Choose an item from the left tree to inspect its APIM policy assignment.</p>
          </div>
        </CardContent>
      </Card>
    )
  }

  return (
    <>
      <Card className="h-full">
        <CardHeader className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="space-y-2">
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <Badge variant={selectedTarget.kind === "api" ? "teal" : "cyan"}>
                {selectedTarget.kind === "api" ? "API" : "Operation"}
              </Badge>
              <span>{selectedTarget.subtitle}</span>
            </div>
            <CardTitle className="text-xl">{selectedTarget.title}</CardTitle>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="outline" onClick={onAssign} disabled={busy || policyLoading}>
              <Wand2 className="h-4 w-4" />
              Assign Template
            </Button>
            <Button
              type="button"
              variant="outline"
              onClick={() => setConfirmClearOpen(true)}
              disabled={busy || policyLoading || !assignment}
            >
              <Trash2 className="h-4 w-4" />
              Clear Assignment
            </Button>
            <Button type="button" variant="ghost" onClick={() => setShowXml((current) => !current)} disabled={policyLoading}>
              <Code2 className="h-4 w-4" />
              {showXml ? "Hide Current XML" : "View Current XML"}
            </Button>
          </div>
        </CardHeader>
        <CardContent className="space-y-6">
          {policyLoading ? (
            <div className="flex items-center gap-2 rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
              <LoaderCircle className="h-4 w-4 animate-spin" />
              Loading policy details…
            </div>
          ) : policyError ? (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
              <div className="flex items-start gap-2">
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                <div className="min-w-0 flex-1">
                  <p>{policyError}</p>
                </div>
                <Button type="button" variant="ghost" size="sm" onClick={onRetry}>
                  <RefreshCcw className="h-3.5 w-3.5" />
                  Retry
                </Button>
              </div>
            </div>
          ) : (
            <>
              <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
                <div className="rounded-lg border p-4">
                  <p className="text-xs uppercase tracking-wide text-muted-foreground">Status</p>
                  <div className="mt-2">
                    <Badge variant={assignmentStatus.variant} className="gap-1.5">
                      <assignmentStatus.icon className={`h-3.5 w-3.5 ${assignmentStatus.spinning ? "animate-spin" : ""}`} />
                      {assignmentStatus.label}
                    </Badge>
                  </div>
                </div>
                <div className="rounded-lg border p-4">
                  <p className="text-xs uppercase tracking-wide text-muted-foreground">Template</p>
                  <p className="mt-2 font-medium">{resolvedTemplateName}</p>
                  <p className="text-sm text-muted-foreground">Version {resolvedTemplateVersion}</p>
                </div>
                <div className="rounded-lg border p-4">
                  <p className="text-xs uppercase tracking-wide text-muted-foreground">Last applied</p>
                  <p className="mt-2 font-medium">{formatDate(assignment?.lastAppliedAt)}</p>
                </div>
                <div className="rounded-lg border p-4">
                  <p className="text-xs uppercase tracking-wide text-muted-foreground">Applied by</p>
                  <p className="mt-2 font-medium">{assignment?.appliedBy || "—"}</p>
                </div>
              </div>

              {detail && (
                <div className={`rounded-lg border p-4 text-sm ${assignment?.status === "failed" ? "border-destructive/50 bg-destructive/10 text-destructive" : "border-amber-400/40 bg-amber-500/10 text-amber-900 dark:text-amber-100"}`}>
                  {detail}
                </div>
              )}

              <div className="rounded-xl border">
                <div className="border-b px-4 py-3">
                  <h3 className="font-medium">Current assignment</h3>
                </div>
                <div className="p-4">
                  {!assignment ? (
                    <div className="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
                      No template is currently assigned.
                    </div>
                  ) : Object.keys(assignment.parameters ?? {}).length === 0 ? (
                    <div className="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
                      This assignment has no parameter overrides.
                    </div>
                  ) : (
                    <dl className="grid gap-3 md:grid-cols-2">
                      {Object.entries(assignment.parameters).map(([key, value]) => (
                        <div key={key} className="rounded-lg border p-3">
                          <dt className="text-xs uppercase tracking-wide text-muted-foreground">{key}</dt>
                          <dd className="mt-1 break-all font-mono text-sm">{value === null ? "null" : String(value)}</dd>
                        </div>
                      ))}
                    </dl>
                  )}
                </div>
              </div>

              {showXml && (
                <div className="rounded-xl border">
                  <div className="border-b px-4 py-3">
                    <h3 className="font-medium">Live APIM XML</h3>
                  </div>
                  <div className="p-4">
                    <pre className="max-h-[360px] overflow-auto rounded-lg bg-slate-950 p-4 text-xs text-slate-50">
                      <code>{policyDocument?.currentXml?.trim() || "No current XML returned."}</code>
                    </pre>
                  </div>
                </div>
              )}
            </>
          )}
        </CardContent>
      </Card>

      <Dialog open={confirmClearOpen} onOpenChange={setConfirmClearOpen}>
        <DialogClose onClose={() => setConfirmClearOpen(false)} />
        <DialogHeader>
          <DialogTitle>Clear policy assignment</DialogTitle>
        </DialogHeader>
        <div className="mt-4 space-y-4">
          <p className="text-sm text-muted-foreground">
            This will replace the policy with passthrough — are you sure?
          </p>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setConfirmClearOpen(false)}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={busy}
              onClick={async () => {
                await onClear()
                setConfirmClearOpen(false)
              }}
            >
              {busy ? "Clearing…" : "Clear Assignment"}
            </Button>
          </div>
        </div>
      </Dialog>
    </>
  )
}

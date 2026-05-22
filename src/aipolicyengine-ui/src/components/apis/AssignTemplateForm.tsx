import { useEffect, useMemo, useState } from "react"
import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { Dialog, DialogClose, DialogHeader, DialogTitle } from "../ui/dialog"
import { Input } from "../ui/input"
import type { ApimTemplateSummary, TemplateParameterDefinition } from "../../types/apim"

interface AssignTemplateFormProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  targetKind: "api" | "operation"
  templates: ApimTemplateSummary[]
  initialTemplateId?: string
  initialParameters?: Record<string, string | number | null>
  parameterDefaults?: Record<string, string | number>
  submitting: boolean
  onSubmit: (payload: { templateId: string; parameters: Record<string, string | number> }) => Promise<void>
}

function toInputValue(value: string | number | null | undefined): string {
  if (value === null || value === undefined) return ""
  return String(value)
}

function buildInitialValues(
  template: ApimTemplateSummary,
  initialParameters: Record<string, string | number | null> | undefined,
  parameterDefaults: Record<string, string | number> | undefined,
): Record<string, string> {
  return Object.fromEntries(
    template.parameters.map((parameter) => {
      const existingValue = initialParameters?.[parameter.name]
      const defaultValue = parameterDefaults?.[parameter.name]
      const fallbackValue = parameter.default
      return [parameter.name, toInputValue(existingValue ?? defaultValue ?? fallbackValue)]
    }),
  )
}

function parameterPlaceholder(parameter: TemplateParameterDefinition): string {
  if (parameter.description) return parameter.description
  return parameter.type === "int" ? "Enter a whole number" : "Enter a value"
}

export function AssignTemplateForm({
  open,
  onOpenChange,
  targetKind,
  templates,
  initialTemplateId,
  initialParameters,
  parameterDefaults,
  submitting,
  onSubmit,
}: AssignTemplateFormProps) {
  const [selectedTemplateId, setSelectedTemplateId] = useState("")
  const [parameterValues, setParameterValues] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)

  const filteredTemplates = useMemo(() => {
    const exactMatches = templates.filter(
      (template) => template.scope === targetKind || template.scope === "both",
    )
    return exactMatches.length > 0 ? exactMatches : templates
  }, [targetKind, templates])

  const selectedTemplate = useMemo(
    () => filteredTemplates.find((template) => template.id === selectedTemplateId) ?? null,
    [filteredTemplates, selectedTemplateId],
  )

  useEffect(() => {
    if (!open) return

    const preferredTemplateId =
      initialTemplateId && filteredTemplates.some((template) => template.id === initialTemplateId)
        ? initialTemplateId
        : filteredTemplates[0]?.id ?? ""

    let cancelled = false
    queueMicrotask(() => {
      if (cancelled) return
      setSelectedTemplateId(preferredTemplateId)

      const template = filteredTemplates.find((item) => item.id === preferredTemplateId)
      setParameterValues(template ? buildInitialValues(template, initialParameters, parameterDefaults) : {})
      setFormError(null)
    })

    return () => {
      cancelled = true
    }
  }, [filteredTemplates, initialParameters, initialTemplateId, open, parameterDefaults])

  const missingRequiredFields = useMemo(() => {
    if (!selectedTemplate) return []

    return selectedTemplate.parameters
      .filter((parameter) => parameter.required && !parameterValues[parameter.name]?.trim())
      .map((parameter) => parameter.name)
  }, [parameterValues, selectedTemplate])

  const handleTemplateChange = (templateId: string) => {
    setSelectedTemplateId(templateId)
    setFormError(null)
    const template = filteredTemplates.find((item) => item.id === templateId)
    setParameterValues(template ? buildInitialValues(template, initialParameters, parameterDefaults) : {})
  }

  const handleApply = async () => {
    if (!selectedTemplate) {
      setFormError("Choose a template before applying.")
      return
    }

    if (missingRequiredFields.length > 0) {
      setFormError(`Complete all required fields: ${missingRequiredFields.join(", ")}`)
      return
    }

    const parameters = Object.fromEntries(
      selectedTemplate.parameters.map((parameter) => {
        const rawValue = parameterValues[parameter.name] ?? ""
        if (parameter.type === "int") {
          return [parameter.name, Number(rawValue)]
        }
        return [parameter.name, rawValue.trim()]
      }),
    )

    await onSubmit({ templateId: selectedTemplate.id, parameters })
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogClose onClose={() => onOpenChange(false)} />
      <DialogHeader>
        <DialogTitle>Assign policy template</DialogTitle>
      </DialogHeader>

      <div className="mt-4 space-y-5">
        <div className="rounded-lg border p-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="text-sm font-medium">1. Choose a template</p>
              <p className="text-xs text-muted-foreground">
                Showing templates usable for {targetKind === "api" ? "API-level" : "operation-level"} assignment.
              </p>
            </div>
            <Badge variant={targetKind === "api" ? "teal" : "cyan"}>{targetKind}</Badge>
          </div>
          <select
            className="mt-3 flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
            value={selectedTemplateId}
            onChange={(event) => handleTemplateChange(event.target.value)}
          >
            {filteredTemplates.length === 0 ? (
              <option value="">No templates available</option>
            ) : (
              filteredTemplates.map((template) => (
                <option key={template.id} value={template.id}>
                  {template.displayName} (v{template.version})
                </option>
              ))
            )}
          </select>
        </div>

        <div className="rounded-lg border p-4">
          <p className="text-sm font-medium">2. Configure parameters</p>
          {!selectedTemplate ? (
            <p className="mt-3 text-sm text-muted-foreground">Select a template to configure its parameters.</p>
          ) : selectedTemplate.parameters.length === 0 ? (
            <p className="mt-3 text-sm text-muted-foreground">This template does not require parameters.</p>
          ) : (
            <div className="mt-4 space-y-4">
              {selectedTemplate.parameters.map((parameter) => (
                <div key={parameter.name} className="min-w-0 space-y-2 rounded-lg border p-3">
                  <div className="flex items-center gap-2">
                    <label htmlFor={`template-parameter-${parameter.name}`} className="min-w-0 flex-1 text-sm font-medium">
                      {parameter.name}
                    </label>
                    {parameter.required && <Badge variant="red" className="flex-shrink-0">Required</Badge>}
                    <Badge variant="outline" className="flex-shrink-0">{parameter.type}</Badge>
                  </div>
                  {parameter.description && (
                    <p className="text-xs text-muted-foreground">{parameter.description}</p>
                  )}
                  <Input
                    id={`template-parameter-${parameter.name}`}
                    type={parameter.type === "int" ? "number" : "text"}
                    step={parameter.type === "int" ? "1" : undefined}
                    value={parameterValues[parameter.name] ?? ""}
                    onChange={(event) => {
                      const nextValue = event.target.value
                      setParameterValues((current) => ({ ...current, [parameter.name]: nextValue }))
                    }}
                    required={parameter.required}
                    placeholder={parameterPlaceholder(parameter)}
                  />
                  {parameter.default !== undefined && parameter.default !== null && (
                    <p className="text-xs text-muted-foreground">Default: {String(parameter.default)}</p>
                  )}
                  {parameterDefaults?.[parameter.name] !== undefined && (
                    <p className="text-xs text-muted-foreground">Default: {String(parameterDefaults[parameter.name])}</p>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="rounded-lg border p-4">
          <p className="text-sm font-medium">3. Apply</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Applying is asynchronous. The page will poll for status updates until the assignment finishes.
          </p>
        </div>

        {formError && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {formError}
          </div>
        )}

        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button
            type="button"
            onClick={() => void handleApply()}
            disabled={submitting || filteredTemplates.length === 0 || missingRequiredFields.length > 0}
          >
            {submitting ? "Applying…" : "Apply Template"}
          </Button>
        </div>
      </div>
    </Dialog>
  )
}

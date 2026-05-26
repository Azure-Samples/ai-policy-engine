export type PolicyAssignmentStatus = "synced" | "pending" | "applying" | "failed"

export type TemplateParameterType = "string" | "int"

export interface ApimApiSummary {
  id: string
  displayName: string
  path: string
  serviceUrl: string
  isCurrent: boolean
}

export interface ApimOperationSummary {
  id: string
  displayName: string
  method: string
  urlTemplate: string
}

export interface TemplateParameterDefinition {
  name: string
  type: TemplateParameterType
  required: boolean
  description: string
  default: string | number | null
}

export interface ApimTemplateSummary {
  id: string
  displayName: string
  version: string
  parameters: TemplateParameterDefinition[]
  scope: string
}

export interface PolicyAssignment {
  id: string
  apiId: string
  operationId: string | null
  apiDisplayName: string
  templateId: string
  templateVersion: string
  parameters: Record<string, string | number | null>
  generatedXmlHash: string | null
  lastAppliedAt: string | null
  appliedBy: string
  status: PolicyAssignmentStatus
  errorMessage: string | null
  createdAt: string
  updatedAt: string
}

export interface ApisResponse {
  apis: ApimApiSummary[]
}

export interface ApiOperationsResponse {
  operations: ApimOperationSummary[]
}

export interface PolicyDocumentResponse {
  assignment: PolicyAssignment | null
  currentXml: string
}

export interface TemplatesResponse {
  templates: ApimTemplateSummary[]
}

export interface ApplyPolicyRequest {
  templateId: string
  parameters: Record<string, string | number>
}

export interface ApplyPolicyResponse {
  assignmentId: string
  status: PolicyAssignmentStatus
}

export interface ClearPolicyResponse {
  status: string
}

export interface HttpError extends Error {
  status?: number
  body?: unknown
}

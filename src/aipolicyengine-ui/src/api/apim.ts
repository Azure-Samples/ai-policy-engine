import { authFetch, parseErrorMessage } from "../api.ts"
import type {
  ApisResponse,
  ApiOperationsResponse,
  ApplyPolicyRequest,
  ApplyPolicyResponse,
  ClearPolicyResponse,
  HttpError,
  PolicyDocumentResponse,
  TemplatesResponse,
} from "../types/apim"

function apiPolicyPath(apiId: string): string {
  return `/api/apim/apis/${encodeURIComponent(apiId)}/policy`
}

function operationPolicyPath(apiId: string, operationId: string): string {
  return `/api/apim/apis/${encodeURIComponent(apiId)}/operations/${encodeURIComponent(operationId)}/policy`
}

async function buildHttpError(res: Response, fallback: string): Promise<HttpError> {
  const cloned = res.clone()
  const message = await parseErrorMessage(res, fallback)
  const error = new Error(message) as HttpError
  error.status = res.status
  error.body = await cloned.json().catch(() => null)
  return error
}

async function requestJson<T>(path: string, fallback: string, options: RequestInit = {}): Promise<T> {
  const res = await authFetch(path, options)
  if (!res.ok) {
    throw await buildHttpError(res, fallback)
  }

  if (res.status === 204) {
    return undefined as T
  }

  return res.json() as Promise<T>
}

export function fetchApimApis(): Promise<ApisResponse> {
  return requestJson<ApisResponse>("/api/apim/apis", "Failed to fetch APIs")
}

export function fetchApimOperations(apiId: string): Promise<ApiOperationsResponse> {
  return requestJson<ApiOperationsResponse>(
    `/api/apim/apis/${encodeURIComponent(apiId)}/operations`,
    "Failed to fetch API operations",
  )
}

export function fetchApiPolicy(apiId: string): Promise<PolicyDocumentResponse> {
  return requestJson<PolicyDocumentResponse>(apiPolicyPath(apiId), "Failed to fetch API policy")
}

export function fetchOperationPolicy(apiId: string, operationId: string): Promise<PolicyDocumentResponse> {
  return requestJson<PolicyDocumentResponse>(
    operationPolicyPath(apiId, operationId),
    "Failed to fetch operation policy",
  )
}

export function fetchApimTemplates(): Promise<TemplatesResponse> {
  return requestJson<TemplatesResponse>("/api/apim/templates", "Failed to fetch policy templates")
}

export function applyApiPolicy(apiId: string, data: ApplyPolicyRequest): Promise<ApplyPolicyResponse> {
  return requestJson<ApplyPolicyResponse>(apiPolicyPath(apiId), "Failed to apply API policy", {
    method: "POST",
    body: JSON.stringify(data),
  })
}

export function applyOperationPolicy(apiId: string, operationId: string, data: ApplyPolicyRequest): Promise<ApplyPolicyResponse> {
  return requestJson<ApplyPolicyResponse>(operationPolicyPath(apiId, operationId), "Failed to apply operation policy", {
    method: "POST",
    body: JSON.stringify(data),
  })
}

export function clearApiPolicy(apiId: string): Promise<ClearPolicyResponse> {
  return requestJson<ClearPolicyResponse>(apiPolicyPath(apiId), "Failed to clear API policy", {
    method: "DELETE",
  })
}

export function clearOperationPolicy(apiId: string, operationId: string): Promise<ClearPolicyResponse> {
  return requestJson<ClearPolicyResponse>(operationPolicyPath(apiId, operationId), "Failed to clear operation policy", {
    method: "DELETE",
  })
}

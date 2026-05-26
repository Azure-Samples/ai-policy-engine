import { authFetch, parseErrorMessage } from "../api"
import type { HttpError } from "../types/apim"
import type {
  AccessProfile,
  AccessProfileCreateRequest,
  AccessProfileUpdateRequest,
  AccessProfilesResponse,
  BulkAccessProfilesRequest,
  BulkAccessProfilesResponse,
} from "../types/accessProfiles"

async function buildHttpError(res: Response, fallback: string): Promise<HttpError> {
  const message = await parseErrorMessage(res, fallback)
  const error = new Error(message) as HttpError
  error.status = res.status
  error.body = await res.clone().json().catch(() => null)
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

function toQueryString(filters: { clientAppId?: string; tenantId?: string; apiId?: string }): string {
  const params = new URLSearchParams()

  if (filters.clientAppId) params.set("clientAppId", filters.clientAppId)
  if (filters.tenantId) params.set("tenantId", filters.tenantId)
  if (filters.apiId) params.set("apiId", filters.apiId)

  const query = params.toString()
  return query ? `?${query}` : ""
}

export function fetchAccessProfiles(filters: { clientAppId?: string; tenantId?: string; apiId?: string }): Promise<AccessProfilesResponse> {
  return requestJson<AccessProfilesResponse>(`/api/access-profiles${toQueryString(filters)}`, "Failed to fetch access profiles")
}

export function fetchAccessProfile(profileId: string): Promise<AccessProfile> {
  return requestJson<AccessProfile>(`/api/access-profiles/${encodeURIComponent(profileId)}`, "Failed to fetch access profile")
}

export function createAccessProfile(data: AccessProfileCreateRequest): Promise<AccessProfile> {
  return requestJson<AccessProfile>("/api/access-profiles", "Failed to create access profile", {
    method: "POST",
    body: JSON.stringify(data),
  })
}

export function updateAccessProfile(profileId: string, data: AccessProfileUpdateRequest): Promise<AccessProfile> {
  return requestJson<AccessProfile>(`/api/access-profiles/${encodeURIComponent(profileId)}`, "Failed to update access profile", {
    method: "PUT",
    body: JSON.stringify(data),
  })
}

export async function deleteAccessProfile(profileId: string): Promise<void> {
  const res = await authFetch(`/api/access-profiles/${encodeURIComponent(profileId)}`, {
    method: "DELETE",
  })

  if (!res.ok) {
    throw await buildHttpError(res, "Failed to delete access profile")
  }
}

export function bulkCreateAccessProfiles(data: BulkAccessProfilesRequest): Promise<BulkAccessProfilesResponse> {
  return requestJson<BulkAccessProfilesResponse>("/api/access-profiles/bulk", "Failed to create access profiles in bulk", {
    method: "POST",
    body: JSON.stringify(data),
  })
}

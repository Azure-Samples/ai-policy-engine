export interface AccessProfile {
  id: string
  partitionKey: string
  clientAppId: string
  tenantId: string
  apiId: string
  operationId: string | null
  planId: string
  routingPolicyId: string | null
  allowedDeployments: string[]
  enabled: boolean
  createdBy: string
  createdAt: string
  updatedAt: string
}

export interface AccessProfileCreateRequest {
  clientAppId: string
  tenantId: string
  apiId: string
  operationId?: string | null
  planId: string
  routingPolicyId?: string | null
  allowedDeployments?: string[]
  enabled?: boolean
}

export interface AccessProfileUpdateRequest {
  planId?: string
  routingPolicyId?: string | null
  allowedDeployments?: string[]
  enabled?: boolean
}

export interface AccessProfilesResponse {
  profiles: AccessProfile[]
}

export interface BulkAccessProfilesRequest {
  profiles: AccessProfileCreateRequest[]
}

export interface BulkAccessProfileFailure {
  index: number
  error: string
  profileId: string | null
}

export interface BulkAccessProfilesResponse {
  created: number
  failed: BulkAccessProfileFailure[]
}

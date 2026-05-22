import type { AccessProfile } from "../../types/accessProfiles"

export type CascadeSource = "direct" | "api" | "global" | "client"

export interface AccessScopeTarget {
  kind: "global" | "api" | "operation"
  apiId: string
  apiDisplayName: string
  operationId: string | null
  operationDisplayName?: string
  method?: string
  urlTemplate?: string
}

export interface EffectiveAccessPreview {
  source: CascadeSource
  sourceLabel: string
  sourceDescription: string
  profileId: string | null
  planId: string
  routingPolicyId: string | null
  allowedDeployments: string[]
  blocked: boolean
  enabled: boolean
}

export interface AccessGridCellData {
  target: AccessScopeTarget
  directProfile: AccessProfile | null
  effective: EffectiveAccessPreview | null
}

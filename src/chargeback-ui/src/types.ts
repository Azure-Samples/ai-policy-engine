export interface LogEntry {
  tenantId: string;
  clientAppId: string;
  audience: string;
  deploymentId: string;
  model: string;
  objectType: string;
  promptTokens: number;
  completionTokens: number;
  imageTokens: number;
  totalTokens: number;
  totalCost: string;
  costToUs: string;
  costToCustomer: string;
  isOverbilled: boolean;
}

// Preferred name going forward — same shape as LogEntry
export type UsageSummary = LogEntry;

export interface UsageSummaryResponse {
  usageSummaries: UsageSummary[];
}

export interface ChargebackResponse {
  totalChargeback: string;
  logs: LogEntry[];
}

export interface RequestLogEntry {
  timestamp: string;
  clientAppId: string;
  clientDisplayName: string;
  tenantId: string;
  deploymentId: string;
  model: string | null;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  costToUs: string;
  costToCustomer: string;
  isOverbilled: boolean;
  statusCode: number;
}

export interface RequestLogsResponse {
  entries: RequestLogEntry[];
  totalCount: number;
}

export interface QuotaData {
  clientAppId: string;
  displayName: string;
  monthlyTokenLimit: number;
  currentUsage: number;
  lastUpdated: string;
}

export interface QuotaUpdateRequest {
  displayName: string;
  monthlyTokenLimit: number;
}

export interface QuotasResponse {
  quotas: QuotaData[];
}

export interface PlanData {
  id: string;
  name: string;
  monthlyRate: number;
  monthlyTokenQuota: number;
  tokensPerMinuteLimit: number;
  requestsPerMinuteLimit: number;
  allowOverbilling: boolean;
  costPerMillionTokens: number;
  rollUpAllDeployments: boolean;
  deploymentQuotas: Record<string, number>;
  allowedDeployments: string[];
  createdAt: string;
  updatedAt: string;
}

export interface PlanCreateRequest {
  name: string;
  monthlyRate: number;
  monthlyTokenQuota: number;
  tokensPerMinuteLimit: number;
  requestsPerMinuteLimit: number;
  allowOverbilling: boolean;
  costPerMillionTokens: number;
  rollUpAllDeployments?: boolean;
  deploymentQuotas?: Record<string, number>;
  allowedDeployments?: string[];
}

export interface PlanUpdateRequest {
  name?: string;
  monthlyRate?: number;
  monthlyTokenQuota?: number;
  tokensPerMinuteLimit?: number;
  requestsPerMinuteLimit?: number;
  allowOverbilling?: boolean;
  costPerMillionTokens?: number;
  rollUpAllDeployments?: boolean;
  deploymentQuotas?: Record<string, number>;
  allowedDeployments?: string[];
}

export interface ClientAssignment {
  clientAppId: string;
  tenantId: string;
  planId: string;
  displayName: string;
  currentPeriodStart: string;
  currentPeriodUsage: number;
  overbilledTokens: number;
  deploymentUsage: Record<string, number>;
  allowedDeployments: string[];
  currentRpm?: number;
  currentTpm?: number;
  lastUpdated: string;
}

export interface ClientAssignRequest {
  planId: string;
  displayName?: string;
  allowedDeployments?: string[];
}

export interface PlansResponse {
  plans: PlanData[];
}

export interface ClientsResponse {
  clients: ClientAssignment[];
}

export interface TraceRecord {
  timestamp: string;
  deploymentId: string;
  model: string | null;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  costToUs: string;
  costToCustomer: string;
  isOverbilled: boolean;
  statusCode: number;
}

export interface ClientUsageResponse {
  assignment: ClientAssignment | null;
  plan: PlanData | null;
  logs: LogEntry[];
  usageByModel: Record<string, number>;
  currentTpm: number;
  currentRpm: number;
  totalCostToUs: number;
  totalCostToCustomer: number;
}

export interface ClientTracesResponse {
  traces: TraceRecord[];
}

export interface ModelPricing {
  modelId: string;
  displayName: string;
  promptRatePer1K: number;
  completionRatePer1K: number;
  imageRatePer1K: number;
  updatedAt: string;
}

export interface ModelPricingCreateRequest {
  modelId: string;
  displayName?: string;
  promptRatePer1K: number;
  completionRatePer1K: number;
  imageRatePer1K: number;
}

export interface ModelPricingResponse {
  models: ModelPricing[];
}

export interface ExportPeriod {
  year: number;
  month: number;
}

export interface ExportClient {
  clientAppId: string;
  tenantId: string;
  displayName: string;
}

export interface ExportPeriodsResponse {
  periods: ExportPeriod[];
  currentPeriod: ExportPeriod;
  clients: ExportClient[];
}

export interface DeploymentInfo {
  id: string;
  model: string;
  modelVersion: string;
  skuName: string;
  skuCapacity: number;
}

export interface DeploymentsResponse {
  deployments: DeploymentInfo[];
}

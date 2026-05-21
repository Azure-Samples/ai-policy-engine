import { initAuth, type AuthProvider, type AuthConfig } from "./auth";
import type { ChargebackResponse, QuotasResponse, QuotaUpdateRequest, QuotaData, PlansResponse, PlanCreateRequest, PlanUpdateRequest, PlanData, ClientsResponse, ClientAssignRequest, ClientUsageResponse, ClientTracesResponse, UsageSummaryResponse, RequestLogsResponse, ModelPricingResponse, ModelPricingCreateRequest, ModelPricing, ExportPeriodsResponse, DeploymentsResponse, RoutingPoliciesResponse, ModelRoutingPolicy, ModelRoutingPolicyCreateRequest, ModelRoutingPolicyUpdateRequest, RequestSummaryResponse } from "./types";

const API_BASE = import.meta.env.VITE_API_URL || "";

// --- Auth initialization (single promise for the whole app) ---
let _authProvider: AuthProvider | null = null;
let _authConfig: AuthConfig | null = null;

/** Initialize auth — call once at app startup, before rendering. */
export async function initializeAuth(): Promise<{ provider: AuthProvider; config: AuthConfig }> {
  const result = await initAuth();
  _authProvider = result.provider;
  _authConfig = result.config;
  return result;
}

export function getResolvedAuthProvider(): AuthProvider | null {
  return _authProvider;
}

export function getResolvedAuthConfig(): AuthConfig | null {
  return _authConfig;
}

async function getToken(): Promise<string | null> {
  if (!_authProvider) return null;
  return _authProvider.getToken();
}

/**
 * Allowed API path prefixes — only paths starting with one of these
 * will be permitted through to fetch, preventing SSRF.
 */
const ALLOWED_PATH_PREFIXES = ["/api/", "/chargeback"];

/**
 * Constructs a safe, absolute URL from a relative path by validating
 * against the allowlist and prepending the hardcoded API_BASE.
 */
function buildApiUrl(path: string): string {
  if (!ALLOWED_PATH_PREFIXES.some((prefix) => path.startsWith(prefix))) {
    throw new Error("Request blocked: path is not in the allowed API routes.");
  }
  return `${API_BASE}${path}`;
}

async function authFetch(path: string, options: RequestInit = {}): Promise<Response> {
  const url = buildApiUrl(path);
  let token: string | null = null;
  try {
    token = await getToken();
  } catch {
    // If token acquisition fails for non-interactive reasons, continue without
    // auth header so the caller gets a normal backend 401/403 response.
    token = null;
  }
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    ...(options.headers as Record<string, string> ?? {}),
  };
  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }
  // Path is validated against ALLOWED_PATH_PREFIXES in buildApiUrl above;
  // this is a client-side browser app — not a server — so SSRF does not apply.
  // nosemgrep: nodejs_scan.javascript-ssrf-rule-node_ssrf
  const res = await fetch(url, { ...options, headers });
  return res;
}

async function parseErrorMessage(res: Response, fallback: string): Promise<string> {
  const body = await res.json().catch(() => null);
  return body?.error || body?.message || `${fallback}: ${res.statusText}`;
}

export async function fetchUsageSummary(): Promise<UsageSummaryResponse> {
  const res = await authFetch(`/api/usage`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch usage summary"));
  return res.json();
}

export async function fetchRequestLogs(): Promise<RequestLogsResponse> {
  const res = await authFetch(`/api/logs`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch request logs"));
  return res.json();
}

export async function fetchChargeback(): Promise<ChargebackResponse> {
  const res = await authFetch(`/chargeback`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch chargeback"));
  return res.json();
}

export async function fetchQuotas(): Promise<QuotasResponse> {
  const res = await authFetch(`/api/quotas`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch quotas"));
  return res.json();
}

export async function updateQuota(clientAppId: string, data: QuotaUpdateRequest): Promise<QuotaData> {
  const res = await authFetch(`/api/quotas/${encodeURIComponent(clientAppId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to update quota"));
  return res.json();
}

export async function deleteQuota(clientAppId: string): Promise<void> {
  const res = await authFetch(`/api/quotas/${encodeURIComponent(clientAppId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to delete quota"));
}

export async function fetchPlans(): Promise<PlansResponse> {
  const res = await authFetch(`/api/plans`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch plans"));
  return res.json();
}

export async function createPlan(data: PlanCreateRequest): Promise<PlanData> {
  const res = await authFetch(`/api/plans`, {
    method: "POST",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to create plan"));
  return res.json();
}

export async function updatePlan(planId: string, data: PlanUpdateRequest): Promise<any> {
  const res = await authFetch(`/api/plans/${encodeURIComponent(planId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to update plan"));
  return res.json();
}

export async function deletePlan(planId: string): Promise<void> {
  const res = await authFetch(`/api/plans/${encodeURIComponent(planId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to delete plan"));
}

export async function fetchClients(): Promise<ClientsResponse> {
  const res = await authFetch(`/api/clients`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch clients"));
  return res.json();
}

export async function assignClient(clientAppId: string, tenantId: string, data: ClientAssignRequest): Promise<any> {
  const res = await authFetch(`/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to assign client"));
  return res.json();
}

export async function removeClient(clientAppId: string, tenantId: string): Promise<void> {
  const res = await authFetch(`/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to remove client"));
}

export async function fetchClientUsage(clientAppId: string, tenantId: string): Promise<ClientUsageResponse> {
  const res = await authFetch(`/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}/usage`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch client usage"));
  return res.json();
}

export async function fetchClientTraces(clientAppId: string, tenantId: string): Promise<ClientTracesResponse> {
  const res = await authFetch(`/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}/traces`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch client traces"));
  return res.json();
}

export async function fetchPricing(): Promise<ModelPricingResponse> {
  const res = await authFetch(`/api/pricing`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch pricing"));
  return res.json();
}

export async function updatePricing(modelId: string, data: ModelPricingCreateRequest): Promise<ModelPricing> {
  const res = await authFetch(`/api/pricing/${encodeURIComponent(modelId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to update pricing"));
  return res.json();
}

export async function deletePricing(modelId: string): Promise<void> {
  const res = await authFetch(`/api/pricing/${encodeURIComponent(modelId)}`, { method: "DELETE" });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to delete pricing"));
}

export function exportCsvUrl(): string {
  return `${API_BASE}/api/export/csv`;
}

export async function fetchDeployments(): Promise<DeploymentsResponse> {
  const res = await authFetch(`/api/deployments`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch deployments"));
  return res.json();
}

export async function fetchExportPeriods(): Promise<ExportPeriodsResponse> {
  const res = await authFetch(`/api/export/available-periods`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch export periods"));
  return res.json();
}

export async function downloadBillingSummary(year: number, month: number): Promise<void> {
  const res = await authFetch(`/api/export/billing-summary?year=${year}&month=${month}`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to download billing summary"));
  await triggerBlobDownload(res);
}

export async function downloadClientAudit(clientAppId: string, tenantId: string, year: number, month: number): Promise<void> {
  const res = await authFetch(`/api/export/client-audit?clientAppId=${encodeURIComponent(clientAppId)}&tenantId=${encodeURIComponent(tenantId)}&year=${year}&month=${month}`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to download client audit"));
  await triggerBlobDownload(res);
}

async function triggerBlobDownload(res: Response): Promise<void> {
  const blob = await res.blob();
  const disposition = res.headers.get("content-disposition") ?? "";
  const filenameMatch = disposition.match(/filename="?([^";\n]+)"?/);
  const filename = filenameMatch?.[1] ?? "export.csv";
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

// --- Phase 4: Model Routing & Multiplier Billing API ---

export async function fetchRoutingPolicies(): Promise<RoutingPoliciesResponse> {
  const res = await authFetch(`/api/routing-policies`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch routing policies"));
  return res.json();
}

export async function fetchRoutingPolicy(policyId: string): Promise<ModelRoutingPolicy> {
  const res = await authFetch(`/api/routing-policies/${encodeURIComponent(policyId)}`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch routing policy"));
  return res.json();
}

export async function createRoutingPolicy(data: ModelRoutingPolicyCreateRequest): Promise<ModelRoutingPolicy> {
  const res = await authFetch(`/api/routing-policies`, {
    method: "POST",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to create routing policy"));
  return res.json();
}

export async function updateRoutingPolicy(policyId: string, data: ModelRoutingPolicyUpdateRequest): Promise<ModelRoutingPolicy> {
  const res = await authFetch(`/api/routing-policies/${encodeURIComponent(policyId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to update routing policy"));
  return res.json();
}

export async function deleteRoutingPolicy(policyId: string): Promise<void> {
  const res = await authFetch(`/api/routing-policies/${encodeURIComponent(policyId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to delete routing policy"));
}

export async function fetchRequestSummary(year?: number, month?: number): Promise<RequestSummaryResponse> {
  const params = new URLSearchParams();
  if (year !== undefined) params.set("year", String(year));
  if (month !== undefined) params.set("month", String(month));
  const qs = params.toString();
  const res = await authFetch(`/api/chargeback/request-summary${qs ? `?${qs}` : ""}`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to fetch request summary"));
  return res.json();
}

export async function downloadRequestBilling(year?: number, month?: number): Promise<void> {
  const params = new URLSearchParams();
  if (year !== undefined) params.set("year", String(year));
  if (month !== undefined) params.set("month", String(month));
  const qs = params.toString();
  const res = await authFetch(`/api/export/request-billing${qs ? `?${qs}` : ""}`);
  if (!res.ok) throw new Error(await parseErrorMessage(res, "Failed to download request billing"));
  await triggerBlobDownload(res);
}

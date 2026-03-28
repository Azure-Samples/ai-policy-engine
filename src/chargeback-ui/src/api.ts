import { InteractionRequiredAuthError, PublicClientApplication, type SilentRequest } from "@azure/msal-browser";
import { msalConfig, loginRequest } from "./auth/msalConfig";
import type { ChargebackResponse, QuotasResponse, QuotaUpdateRequest, QuotaData, PlansResponse, PlanCreateRequest, PlanUpdateRequest, PlanData, ClientsResponse, ClientAssignRequest, ClientUsageResponse, ClientTracesResponse, UsageSummaryResponse, RequestLogsResponse, ModelPricingResponse, ModelPricingCreateRequest, ModelPricing, ExportPeriodsResponse, DeploymentsResponse } from "./types";

const API_BASE = import.meta.env.VITE_API_URL || "";

const msalInstance = new PublicClientApplication(msalConfig);
let redirectInFlight = false;

// Initialize MSAL and handle redirect/popup responses on page load.
// This is critical — without it, popup auth flow will hang.
const msalReady = msalInstance.initialize().then(() => {
  return msalInstance.handleRedirectPromise();
});

function isInteractionInProgress(): boolean {
  return redirectInFlight || window.sessionStorage.getItem("msal.interaction.status") === "interaction_in_progress";
}

async function startRedirectOnce(): Promise<void> {
  if (isInteractionInProgress()) return;
  redirectInFlight = true;
  await msalInstance.acquireTokenRedirect(loginRequest);
}

async function getToken(): Promise<string | null> {
  await msalReady;
  const accounts = msalInstance.getAllAccounts();
  if (accounts.length === 0) return null;
  try {
    const request: SilentRequest = { ...loginRequest, account: accounts[0] };
    const response = await msalInstance.acquireTokenSilent(request);
    return response.accessToken;
  } catch (error) {
    // Only initiate interactive auth when MSAL explicitly requires it.
    // Avoid re-entrant redirects that cause interaction_in_progress loops.
    if (error instanceof InteractionRequiredAuthError) {
      await startRedirectOnce();
      return null;
    }
    if ((error as { errorCode?: string })?.errorCode === "interaction_in_progress") {
      return null;
    }
    throw error;
  } finally {
    if (!isInteractionInProgress()) {
      redirectInFlight = false;
    }
  }
}

async function authFetch(url: string, options: RequestInit = {}): Promise<Response> {
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
  const res = await fetch(url, { ...options, headers });
  return res;
}

export async function fetchUsageSummary(): Promise<UsageSummaryResponse> {
  const res = await authFetch(`${API_BASE}/api/usage`);
  if (!res.ok) throw new Error(`Failed to fetch usage summary: ${res.statusText}`);
  return res.json();
}

export async function fetchRequestLogs(): Promise<RequestLogsResponse> {
  const res = await authFetch(`${API_BASE}/api/logs`);
  if (!res.ok) throw new Error(`Failed to fetch request logs: ${res.statusText}`);
  return res.json();
}

export async function fetchChargeback(): Promise<ChargebackResponse> {
  const res = await authFetch(`${API_BASE}/chargeback`);
  if (!res.ok) throw new Error(`Failed to fetch chargeback: ${res.statusText}`);
  return res.json();
}

export async function fetchQuotas(): Promise<QuotasResponse> {
  const res = await authFetch(`${API_BASE}/api/quotas`);
  if (!res.ok) throw new Error(`Failed to fetch quotas: ${res.statusText}`);
  return res.json();
}

export async function updateQuota(clientAppId: string, data: QuotaUpdateRequest): Promise<QuotaData> {
  const res = await authFetch(`${API_BASE}/api/quotas/${encodeURIComponent(clientAppId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to update quota: ${res.statusText}`);
  return res.json();
}

export async function deleteQuota(clientAppId: string): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/quotas/${encodeURIComponent(clientAppId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(`Failed to delete quota: ${res.statusText}`);
}

export async function fetchPlans(): Promise<PlansResponse> {
  const res = await authFetch(`${API_BASE}/api/plans`);
  if (!res.ok) throw new Error(`Failed to fetch plans: ${res.statusText}`);
  return res.json();
}

export async function createPlan(data: PlanCreateRequest): Promise<PlanData> {
  const res = await authFetch(`${API_BASE}/api/plans`, {
    method: "POST",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to create plan: ${res.statusText}`);
  return res.json();
}

export async function updatePlan(planId: string, data: PlanUpdateRequest): Promise<any> {
  const res = await authFetch(`${API_BASE}/api/plans/${encodeURIComponent(planId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to update plan: ${res.statusText}`);
  return res.json();
}

export async function deletePlan(planId: string): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/plans/${encodeURIComponent(planId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(`Failed to delete plan: ${res.statusText}`);
}

export async function fetchClients(): Promise<ClientsResponse> {
  const res = await authFetch(`${API_BASE}/api/clients`);
  if (!res.ok) throw new Error(`Failed to fetch clients: ${res.statusText}`);
  return res.json();
}

export async function assignClient(clientAppId: string, tenantId: string, data: ClientAssignRequest): Promise<any> {
  const res = await authFetch(`${API_BASE}/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to assign client: ${res.statusText}`);
  return res.json();
}

export async function removeClient(clientAppId: string, tenantId: string): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}`, {
    method: "DELETE",
  });
  if (!res.ok) throw new Error(`Failed to remove client: ${res.statusText}`);
}

export async function fetchClientUsage(clientAppId: string, tenantId: string): Promise<ClientUsageResponse> {
  const res = await authFetch(`${API_BASE}/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}/usage`);
  if (!res.ok) throw new Error(`Failed to fetch client usage: ${res.statusText}`);
  return res.json();
}

export async function fetchClientTraces(clientAppId: string, tenantId: string): Promise<ClientTracesResponse> {
  const res = await authFetch(`${API_BASE}/api/clients/${encodeURIComponent(clientAppId)}/${encodeURIComponent(tenantId)}/traces`);
  if (!res.ok) throw new Error(`Failed to fetch client traces: ${res.statusText}`);
  return res.json();
}

export async function fetchPricing(): Promise<ModelPricingResponse> {
  const res = await authFetch(`${API_BASE}/api/pricing`);
  if (!res.ok) throw new Error(`Failed to fetch pricing: ${res.statusText}`);
  return res.json();
}

export async function updatePricing(modelId: string, data: ModelPricingCreateRequest): Promise<ModelPricing> {
  const res = await authFetch(`${API_BASE}/api/pricing/${encodeURIComponent(modelId)}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to update pricing: ${res.statusText}`);
  return res.json();
}

export async function deletePricing(modelId: string): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/pricing/${encodeURIComponent(modelId)}`, { method: "DELETE" });
  if (!res.ok) throw new Error(`Failed to delete pricing: ${res.statusText}`);
}

export function exportCsvUrl(): string {
  return `${API_BASE}/api/export/csv`;
}

export async function fetchDeployments(): Promise<DeploymentsResponse> {
  const res = await authFetch(`${API_BASE}/api/deployments`);
  if (!res.ok) throw new Error(`Failed to fetch deployments: ${res.statusText}`);
  return res.json();
}

export async function fetchExportPeriods(): Promise<ExportPeriodsResponse> {
  const res = await authFetch(`${API_BASE}/api/export/available-periods`);
  if (!res.ok) throw new Error(`Failed to fetch export periods: ${res.statusText}`);
  return res.json();
}

export async function downloadBillingSummary(year: number, month: number): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/export/billing-summary?year=${year}&month=${month}`);
  if (!res.ok) throw new Error(`Failed to download billing summary: ${res.statusText}`);
  await triggerBlobDownload(res);
}

export async function downloadClientAudit(clientAppId: string, tenantId: string, year: number, month: number): Promise<void> {
  const res = await authFetch(`${API_BASE}/api/export/client-audit?clientAppId=${encodeURIComponent(clientAppId)}&tenantId=${encodeURIComponent(tenantId)}&year=${year}&month=${month}`);
  if (!res.ok) throw new Error(`Failed to download client audit: ${res.statusText}`);
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

export { msalInstance, msalReady };

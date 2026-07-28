/**
 * Auth provider abstraction — allows switching between MSAL (Azure AD)
 * and OIDC (Keycloak) based on the runtime /api/auth-config endpoint.
 */

export interface AuthProvider {
  /** Initialize the auth provider (handle redirects, etc.) */
  initialize(): Promise<void>;
  /** Get an access token for API calls, or null if not authenticated */
  getToken(): Promise<string | null>;
  /** Check if the user is currently authenticated */
  isAuthenticated(): boolean;
  /** Trigger interactive login */
  login(): Promise<void>;
  /** Trigger logout */
  logout(): Promise<void>;
  /** Get the display name of the current user */
  getUserDisplayName(): string | null;
  /**
   * Return the current user's roles as a flat string array.
   * For Azure AD, reads from the ID token's "roles" claim.
   * For Keycloak, decodes the access token and unions realm_access.roles
   * with all resource_access.<client>.roles entries.
   */
  getRoles(): string[];
}

export type AuthProviderType = "AzureAd" | "Keycloak";

export interface AuthConfig {
  authProvider: AuthProviderType;
  clientId: string;
  authority: string;
  audience: string;
  // AzureAd-specific
  tenantId?: string;
  scope?: string;
  // Keycloak-specific
  realm?: string;
  frontendUrl?: string;
}

const API_BASE = import.meta.env.VITE_API_URL || "";

let _cachedConfig: AuthConfig | null = null;

/**
 * Fetch auth configuration from the backend at runtime.
 * Result is cached — only one network call per page load.
 */
export async function fetchAuthConfig(): Promise<AuthConfig> {
  if (_cachedConfig) return _cachedConfig;
  const res = await fetch(`${API_BASE}/api/auth-config`);
  if (!res.ok) {
    throw new Error(`Failed to load auth config: ${res.statusText}`);
  }
  _cachedConfig = await res.json();
  return _cachedConfig!;
}

export function getCachedAuthConfig(): AuthConfig | null {
  return _cachedConfig;
}

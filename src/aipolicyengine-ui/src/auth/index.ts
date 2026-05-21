import type { AuthProvider } from "./authProvider";
import { fetchAuthConfig, type AuthConfig } from "./authProvider";

export type { AuthProvider } from "./authProvider";
export type { AuthProviderType, AuthConfig } from "./authProvider";
export { fetchAuthConfig, getCachedAuthConfig } from "./authProvider";

let _authProvider: AuthProvider | null = null;
let _resolvedConfig: AuthConfig | null = null;

/**
 * Fetch runtime config from the backend, then create and initialize
 * the correct auth provider. Returns both the provider and the config.
 */
export async function initAuth(): Promise<{ provider: AuthProvider; config: AuthConfig }> {
  if (_authProvider && _resolvedConfig) {
    return { provider: _authProvider, config: _resolvedConfig };
  }

  const config = await fetchAuthConfig();
  _resolvedConfig = config;

  if (config.authProvider === "Keycloak") {
    const { createKeycloakAuthProvider } = await import("./keycloakAuth");
    _authProvider = createKeycloakAuthProvider(config);
  } else {
    const { createMsalAuthProvider } = await import("./msalAuth");
    _authProvider = createMsalAuthProvider(config);
  }

  await _authProvider.initialize();
  return { provider: _authProvider, config };
}

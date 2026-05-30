import { InteractionRequiredAuthError, PublicClientApplication, type SilentRequest } from "@azure/msal-browser";
import type { AuthProvider, AuthConfig } from "./authProvider";

let msalInstance: PublicClientApplication | null = null;
let scopes: string[] = [];
let redirectInFlight = false;

function isInteractionInProgress(): boolean {
  return redirectInFlight || window.sessionStorage.getItem("msal.interaction.status") === "interaction_in_progress";
}

export function createMsalAuthProvider(config: AuthConfig): AuthProvider {
  msalInstance = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: "localStorage" },
  });

  scopes = config.scope ? [config.scope] : [];

  const instance = msalInstance;
  const loginRequest = { scopes };

  return {
    async initialize(): Promise<void> {
      await instance.initialize();
      await instance.handleRedirectPromise();
    },

    async getToken(): Promise<string | null> {
      const accounts = instance.getAllAccounts();
      if (accounts.length === 0) return null;
      try {
        const request: SilentRequest = { ...loginRequest, account: accounts[0] };
        const response = await instance.acquireTokenSilent(request);
        return response.accessToken;
      } catch (error) {
        if (error instanceof InteractionRequiredAuthError) {
          if (!isInteractionInProgress()) {
            redirectInFlight = true;
            await instance.acquireTokenRedirect(loginRequest);
          }
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
    },

    isAuthenticated(): boolean {
      return instance.getAllAccounts().length > 0;
    },

    async login(): Promise<void> {
      await instance.loginRedirect(loginRequest);
    },

    async logout(): Promise<void> {
      await instance.logoutRedirect();
    },

    getUserDisplayName(): string | null {
      const accounts = instance.getAllAccounts();
      return accounts.length > 0 ? accounts[0].name ?? accounts[0].username : null;
    },

    getRoles(): string[] {
      const accounts = instance.getAllAccounts();
      if (accounts.length === 0) return [];
      const roles = accounts[0]?.idTokenClaims?.["roles"];
      return Array.isArray(roles) ? (roles as string[]) : [];
    },
  };
}

/** Expose the raw MSAL instance for MsalProvider (React context) */
export function getMsalInstance(): PublicClientApplication | null {
  return msalInstance;
}

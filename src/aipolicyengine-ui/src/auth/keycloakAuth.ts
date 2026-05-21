import { UserManager, WebStorageStateStore, type User } from "oidc-client-ts";
import type { AuthProvider, AuthConfig } from "./authProvider";

let userManager: UserManager | null = null;
let currentUser: User | null = null;

export function createKeycloakAuthProvider(config: AuthConfig): AuthProvider {
  // Build authority from components if not directly provided
  const authority = config.authority
    || (config.frontendUrl && config.realm ? `${config.frontendUrl}/realms/${config.realm}` : "");

  userManager = new UserManager({
    authority,
    client_id: config.clientId,
    redirect_uri: window.location.origin,
    post_logout_redirect_uri: window.location.origin,
    response_type: "code",
    response_mode: "fragment",
    scope: "openid profile email",
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    automaticSilentRenew: true,
    silent_redirect_uri: window.location.origin,
  });

  const mgr = userManager;

  return {
    async initialize(): Promise<void> {
      // Handle callback redirect (fragment mode: params are in hash, not query string)
      const hasCallback = window.location.hash.includes("code=") || window.location.hash.includes("state=")
        || window.location.search.includes("code=") || window.location.search.includes("state=");
      if (hasCallback) {
        try {
          currentUser = await mgr.signinRedirectCallback();
          window.history.replaceState({}, document.title, window.location.pathname);
        } catch {
          currentUser = await mgr.getUser();
        }
      } else {
        currentUser = await mgr.getUser();
      }

      mgr.events.addUserLoaded((user) => {
        currentUser = user;
      });
      mgr.events.addUserUnloaded(() => {
        currentUser = null;
      });
    },

    async getToken(): Promise<string | null> {
      if (!currentUser || currentUser.expired) {
        try {
          currentUser = await mgr.signinSilent();
        } catch {
          return null;
        }
      }
      return currentUser?.access_token ?? null;
    },

    isAuthenticated(): boolean {
      return currentUser != null && !currentUser.expired;
    },

    async login(): Promise<void> {
      await mgr.signinRedirect();
    },

    async logout(): Promise<void> {
      await mgr.signoutRedirect();
    },

    getUserDisplayName(): string | null {
      return currentUser?.profile?.preferred_username
        ?? currentUser?.profile?.name
        ?? null;
    },
  };
}

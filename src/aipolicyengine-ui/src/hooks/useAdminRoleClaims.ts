import { useMemo } from "react"
import { getResolvedAuthProvider } from "../api"

/**
 * Returns the current user's roles as a flat string array, sourced from the
 * active auth provider regardless of mode:
 *   - Azure AD: reads from the ID token's "roles" app-role claim.
 *   - Keycloak: decodes the access token and unions realm_access.roles with
 *               all resource_access.<client>.roles entries.
 *
 * The result is memoised for the lifetime of the component — roles do not
 * change mid-session.
 */
export function useAdminRoleClaims(): string[] {
  return useMemo(() => getResolvedAuthProvider()?.getRoles() ?? [], [])
}

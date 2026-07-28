import { useContext } from "react"
import { MsalContext } from "@azure/msal-react"

/**
 * Safe wrapper around the MSAL context that does NOT throw when called
 * outside of MsalProvider (e.g. in Keycloak auth mode).
 *
 * The underlying useMsal() hook throws an invariant error when there is no
 * MsalProvider ancestor. Using useContext(MsalContext) directly avoids that
 * check and returns the default context value which has accounts: [].
 *
 * This lets pages like AccessProfiles and Apis be rendered in both Azure AD
 * and Keycloak modes without crashing.
 */
export function useSafeMsal() {
  return useContext(MsalContext)
}

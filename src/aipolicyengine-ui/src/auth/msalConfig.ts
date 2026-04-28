import { type Configuration } from "@azure/msal-browser";

const tenantId = import.meta.env.VITE_AZURE_TENANT_ID ?? "";
const clientId = import.meta.env.VITE_AZURE_CLIENT_ID ?? "";
const authority =
  import.meta.env.VITE_AZURE_AUTHORITY ??
  (tenantId ? `https://login.microsoftonline.com/${tenantId}` : "");
const scope =
  import.meta.env.VITE_AZURE_SCOPE ??
  (import.meta.env.VITE_AZURE_API_APP_ID
    ? `api://${import.meta.env.VITE_AZURE_API_APP_ID}/access_as_user`
    : "");

export const msalConfig: Configuration = {
  auth: {
    clientId,
    authority,
    redirectUri: window.location.origin,
  },
  cache: { cacheLocation: "localStorage" },
};

export const loginRequest = {
  scopes: scope ? [scope] : [],
};

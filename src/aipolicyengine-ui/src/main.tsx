import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import { ThemeProvider } from './context/ThemeProvider'
import { initializeAuth } from './api'
import './index.css'
import App from './App.tsx'

// Fetch runtime auth config from the backend, then initialize the correct
// auth provider before rendering the React tree.
initializeAuth().then(({ config }) => {
  const app = (
    <StrictMode>
      <ThemeProvider>
        <App />
      </ThemeProvider>
    </StrictMode>
  );

  if (config.authProvider === "AzureAd") {
    // Lazy import to avoid loading MSAL when using Keycloak
    import('./auth/msalAuth').then(({ getMsalInstance }) => {
      const msalInstance = getMsalInstance();
      if (msalInstance) {
        createRoot(document.getElementById('root')!).render(
          <StrictMode>
            <MsalProvider instance={msalInstance}>
              <ThemeProvider>
                <App />
              </ThemeProvider>
            </MsalProvider>
          </StrictMode>
        );
      } else {
        createRoot(document.getElementById('root')!).render(app);
      }
    });
  } else {
    createRoot(document.getElementById('root')!).render(app);
  }
})

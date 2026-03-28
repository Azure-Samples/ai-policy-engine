import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import { ThemeProvider } from './context/ThemeProvider'
import { msalInstance, msalReady } from './api'
import './index.css'
import App from './App.tsx'

// Wait for MSAL to initialize and handle any auth redirects before rendering
msalReady.then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <MsalProvider instance={msalInstance}>
        <ThemeProvider>
          <App />
        </ThemeProvider>
      </MsalProvider>
    </StrictMode>,
  )
})

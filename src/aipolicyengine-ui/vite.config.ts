import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // Emit directly into the API's wwwroot so the .NET SDK static web assets
    // pipeline picks the SPA up automatically during `dotnet publish`.
    outDir: '../AIPolicyEngine.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/chargeback': 'http://localhost:5057',
      '/api': 'http://localhost:5057',
    }
  }
})

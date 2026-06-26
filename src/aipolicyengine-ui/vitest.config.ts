import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'node', // node is fine for pure function tests; jsdom for component tests
    globals: true,
    setupFiles: [],
  },
})

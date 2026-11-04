import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Dev proxy target; override with VITE_BACKEND when port 8080 is taken on the dev machine.
const backend = process.env.VITE_BACKEND ?? 'http://localhost:8080'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: backend, changeOrigin: true },
      '/hubs': { target: backend, changeOrigin: true, ws: true },
      '/files': { target: backend, changeOrigin: true },
      '/tus': { target: backend, changeOrigin: true },
    },
  },
})

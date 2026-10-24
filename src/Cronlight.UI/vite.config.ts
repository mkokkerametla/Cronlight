import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The dashboard is served from whatever prefix the host passes to MapCronlight,
// so every asset and API URL must be relative.
export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  build: {
    // Embedded into the Cronlight assembly as resources
    outDir: '../Cronlight/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // `npm run dev` against a running host, e.g. samples/SampleHost
    proxy: {
      '/api': {
        target: process.env.DASHBOARD_URL ?? 'http://localhost:5099/ops/jobs',
        changeOrigin: true,
        configure: proxy => {
          proxy.on('proxyReq', req => {
            if (process.env.DASHBOARD_ROLE) req.setHeader('X-Demo-Role', process.env.DASHBOARD_ROLE)
          })
        },
      },
    },
  },
})

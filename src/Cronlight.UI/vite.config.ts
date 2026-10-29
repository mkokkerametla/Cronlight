import { readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { brotliCompressSync, constants, gzipSync } from 'node:zlib'
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

/**
 * Writes brotli and gzip copies of the scripts and styles next to them, which the dashboard sends to browsers that
 * accept them: compressed once here, rather than on every request by the host. Browsers ask for brotli over HTTPS
 * only, so plain HTTP inside a network gets gzip.
 */
function precompress(): Plugin {
  return {
    name: 'cronlight-precompress',
    apply: 'build',
    writeBundle(options, bundle) {
      for (const name of Object.keys(bundle).filter(name => /\.(js|css)$/.test(name))) {
        const file = path.join(options.dir!, name)
        const content = readFileSync(file)
        writeFileSync(
          `${file}.br`,
          brotliCompressSync(content, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } })
        )
        writeFileSync(`${file}.gz`, gzipSync(content, { level: 9 }))
      }
    },
  }
}

// The dashboard is served from whatever prefix the host passes to MapCronlight,
// so every asset and API URL must be relative.
export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss(), precompress()],
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

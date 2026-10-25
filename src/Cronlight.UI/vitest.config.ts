import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config'

// Dates and times in tests are checked in UTC, whatever the machine's time zone
process.env.TZ = 'UTC'

// Unit and component tests; the end-to-end tests live in tests/Cronlight.E2E
export default mergeConfig(
  viteConfig,
  defineConfig({
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}'],
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      coverage: {
        provider: 'v8',
        include: ['src/**/*.{ts,tsx}'],
        // shadcn/ui components are generated code owned by the library
        exclude: ['src/components/ui/**', 'src/test/**', 'src/**/*.test.{ts,tsx}', 'src/main.tsx'],
      },
    },
  })
)

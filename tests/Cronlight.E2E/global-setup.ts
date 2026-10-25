import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const sampleHostDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../samples/Cronlight.SampleHost')
// The sample's TargetFramework
export const sampleHostDll = path.join(sampleHostDir, 'bin/Release/net10.0/Cronlight.SampleHost.dll')

/**
 * Builds the sample host (and the dashboard UI it embeds) once, before any worker starts one. A build with
 * nothing changed takes a few seconds. Set E2E_SKIP_BUILD=1 to reuse what is already built.
 * No build servers: MSBuild nodes and the compiler server would otherwise stay in memory for minutes,
 * alongside the hosts and browsers the tests start. The build is a little slower for it.
 */
export default function globalSetup() {
  if (process.env.E2E_SKIP_BUILD) return
  execFileSync(
    'dotnet',
    ['build', sampleHostDir, '-c', 'Release', '--nologo', '-v', 'quiet', '--disable-build-servers', '-nodeReuse:false'],
    { stdio: 'inherit' }
  )
}

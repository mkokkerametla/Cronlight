import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'

/** A webhook post the sample host sent: the path it went to (the webhook's format) and its JSON body. */
export interface WebhookPost {
  path: string
  body: Record<string, unknown>
}

/**
 * Receives the alerts the sample host posts, as a JSON, Slack or Teams endpoint would. Each worker's host posts to
 * its own receiver, at /json, /slack and /teams, one webhook of each format.
 */
export class WebhookReceiver {
  readonly posts: WebhookPost[] = []
  private constructor(private readonly server: Server) {}

  static async start() {
    const server = createServer((request, response) => {
      let body = ''
      request.on('data', chunk => (body += chunk))
      request.on('end', () => {
        receiver.posts.push({ path: request.url ?? '', body: JSON.parse(body) })
        response.writeHead(204).end()
      })
    })
    const receiver = new WebhookReceiver(server)
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
    return receiver
  }

  get url() {
    return `http://127.0.0.1:${(this.server.address() as AddressInfo).port}`
  }

  /** The sample host's settings for posting to this receiver, as environment variables. */
  get settings(): Record<string, string> {
    return Object.fromEntries(
      ['Json', 'Slack', 'Teams'].flatMap((format, i) => [
        [`CronlightAlerts__Webhooks__${i}__Url`, `${this.url}/${format.toLowerCase()}`],
        [`CronlightAlerts__Webhooks__${i}__Format`, format],
      ])
    )
  }

  /** The JSON alerts received for a job, oldest first. Tests share the host, so each looks at its own jobs only. */
  alertsFor(name: string) {
    return this.posts.filter(p => p.path === '/json' && p.body.jobName === name).map(p => p.body)
  }

  /** What the Slack and Teams webhooks received for a job, oldest first. */
  chatPostsFor(name: string, path: '/slack' | '/teams') {
    // Their titles name the job as "Job <group>.<name> failed", or "succeeded again"
    return this.posts.filter(p => p.path === path && JSON.stringify(p.body).includes(`.${name} `)).map(p => p.body)
  }

  close() {
    return new Promise<void>(resolve => this.server.close(() => resolve()))
  }
}

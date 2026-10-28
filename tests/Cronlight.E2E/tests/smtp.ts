import { createServer, type AddressInfo, type Server, type Socket } from 'node:net'

/** An email the sample host sent: who to, and its subject and body, decoded. */
export interface Email {
  to: string[]
  subject: string
  body: string
}

/**
 * Receives the alerts the sample host emails, as an SMTP relay on a trusted network would: plain connections, no
 * sign-in. Each worker's host sends to its own receiver.
 */
export class SmtpReceiver {
  readonly emails: Email[] = []
  private readonly sockets = new Set<Socket>()
  private constructor(private readonly server: Server) {}

  static async start() {
    const server = createServer(socket => receiver.serve(socket))
    const receiver = new SmtpReceiver(server)
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
    return receiver
  }

  /** The sample host's settings for emailing alerts to this receiver, as environment variables. */
  get settings(): Record<string, string> {
    return {
      CronlightAlerts__Email__Host: '127.0.0.1',
      CronlightAlerts__Email__Port: String((this.server.address() as AddressInfo).port),
      CronlightAlerts__Email__UseTls: 'false',
      CronlightAlerts__Email__From: 'Cronlight <cronlight@example.com>',
      CronlightAlerts__Email__To__0: 'oncall@example.com',
    }
  }

  /** The emails about a job, oldest first: their subjects name it as "Job <group>.<name> ..." */
  emailsFor(name: string) {
    return this.emails.filter(e => e.subject.includes(`.${name} `))
  }

  private serve(socket: Socket) {
    this.sockets.add(socket)
    socket.on('close', () => this.sockets.delete(socket))
    socket.on('error', () => socket.destroy())
    const reply = (line: string) => socket.write(`${line}\r\n`)
    let buffer = ''
    let data: string[] | null = null
    let to: string[] = []

    reply('220 localhost ESMTP')
    socket.on('data', chunk => {
      buffer += chunk.toString('latin1')
      for (let end = buffer.indexOf('\r\n'); end >= 0; end = buffer.indexOf('\r\n')) {
        const line = buffer.slice(0, end)
        buffer = buffer.slice(end + 2)
        if (data) {
          if (line !== '.') {
            data.push(line.startsWith('..') ? line.slice(1) : line)
            continue
          }
          this.emails.push(parse(to, data.join('\r\n')))
          data = null
          to = []
          reply('250 OK')
          continue
        }
        switch (line.split(' ')[0].toUpperCase()) {
          case 'EHLO':
            reply('250-localhost')
            reply('250 8BITMIME')
            break
          case 'RCPT':
            to.push(/<(.*)>/.exec(line)?.[1] ?? '')
            reply('250 OK')
            break
          case 'DATA':
            data = []
            reply('354 End data with <CR><LF>.<CR><LF>')
            break
          case 'QUIT':
            reply('221 Bye')
            socket.end()
            break
          default:
            reply('250 OK')
        }
      }
    })
  }

  close() {
    for (const socket of this.sockets) socket.destroy()
    return new Promise<void>(resolve => this.server.close(() => resolve()))
  }
}

/** The message's subject and body, from base64 or quoted-printable, as .NET's MailMessage encodes UTF-8 text. */
function parse(to: string[], raw: string): Email {
  const split = raw.indexOf('\r\n\r\n')
  // Folded header lines continue with whitespace
  const headers = raw.slice(0, split).replace(/\r\n[ \t]+/g, ' ')
  const header = (name: string) => new RegExp(`^${name}:\\s*(.*)$`, 'im').exec(headers)?.[1].trim() ?? ''
  const subject = header('Subject').replace(/=\?([^?]+)\?([BbQq])\?([^?]*)\?=\s*/g, (_, _charset, kind: string, text: string) =>
    kind.toUpperCase() === 'B' ? Buffer.from(text, 'base64').toString('utf8') : quotedPrintable(text.replace(/_/g, ' '))
  )
  let body = raw.slice(split + 4)
  const encoding = header('Content-Transfer-Encoding').toLowerCase()
  if (encoding === 'base64') body = Buffer.from(body.replace(/\s/g, ''), 'base64').toString('utf8')
  else if (encoding === 'quoted-printable') body = quotedPrintable(body.replace(/=\r\n/g, ''))
  return { to, subject, body: body.replace(/\r\n/g, '\n') }
}

function quotedPrintable(text: string) {
  const bytes: number[] = []
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '=' && /^[0-9A-Fa-f]{2}$/.test(text.slice(i + 1, i + 3))) {
      bytes.push(parseInt(text.slice(i + 1, i + 3), 16))
      i += 2
    } else bytes.push(...Buffer.from(text[i], 'latin1'))
  }
  return Buffer.from(bytes).toString('utf8')
}

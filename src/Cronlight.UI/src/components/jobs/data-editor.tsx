import { Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { MASK, SECRET_PREFIX, type DataRow } from '@/lib/job-form'

export function DataEditor({
  rows,
  onChange,
  secretReferences = false,
}: {
  rows: DataRow[]
  onChange: (rows: DataRow[]) => void
  /** The server resolves secret:<name> values in secret fields from its configuration */
  secretReferences?: boolean
}) {
  const update = (index: number, patch: Partial<DataRow>) =>
    onChange(rows.map((r, i) => (i === index ? { ...r, ...patch } : r)))

  return (
    <div className="space-y-3">
      {rows.map((row, index) =>
        row.param ? (
          <div key={index} className="space-y-1.5">
            <Label htmlFor={`param-${row.key}`} className="font-mono text-xs">
              {row.key}
              {row.param.required && <span className="text-destructive">*</span>}
            </Label>
            <ValueInput
              id={`param-${row.key}`}
              row={row}
              secretReferences={secretReferences}
              onChange={value => update(index, { value })}
            />
            {row.param.description && (
              <p className="text-xs text-muted-foreground">{row.param.description}</p>
            )}
            {secretReferences && row.param.type === 'Secret' && (
              <p className="text-xs text-muted-foreground">
                Or enter <code className="font-mono">{SECRET_PREFIX}name</code> to use a secret
                stored in the server&apos;s configuration, so the value is never saved with the job.
              </p>
            )}
          </div>
        ) : (
          <div key={index} className="flex gap-2">
            <Input
              className="w-2/5 font-mono text-xs"
              value={row.key}
              onChange={e => update(index, { key: e.target.value })}
              placeholder="key"
              aria-label="Data key"
            />
            <ValueInput
              row={row}
              secretReferences={secretReferences}
              onChange={value => update(index, { value })}
            />
            <Button
              type="button"
              variant="ghost"
              size="icon"
              onClick={() => onChange(rows.filter((_, i) => i !== index))}
              aria-label={`Remove ${row.key || 'entry'}`}
            >
              <X />
            </Button>
          </div>
        )
      )}
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => onChange([...rows, { key: '', value: '' }])}
      >
        <Plus /> Add entry
      </Button>
      {rows.some(r => r.value === MASK) && (
        <p className="text-xs text-muted-foreground">
          Masked values stay as they are unless you replace them.
        </p>
      )}
    </div>
  )
}

function ValueInput({
  id,
  row,
  secretReferences,
  onChange,
}: {
  id?: string
  row: DataRow
  secretReferences: boolean
  onChange: (value: string) => void
}) {
  const type = row.param?.type ?? (row.value === MASK ? 'Secret' : 'String')
  // A reference only names a secret, so it is shown rather than hidden like a password
  const isReference = secretReferences && row.value.startsWith(SECRET_PREFIX)
  const common = {
    id,
    value: row.value,
    required: row.param?.required,
    'aria-label': id ? undefined : `Value for ${row.key || 'entry'}`,
  }

  if (row.param?.choices?.length)
    return (
      <Select value={row.value || undefined} onValueChange={onChange}>
        <SelectTrigger id={id} className="w-full">
          <SelectValue placeholder="Choose…" />
        </SelectTrigger>
        <SelectContent>
          {row.param.choices.map(choice => (
            <SelectItem key={choice} value={choice}>
              {choice}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    )

  switch (type) {
    case 'Bool':
      return (
        <Select value={row.value || undefined} onValueChange={onChange}>
          <SelectTrigger id={id} className="w-full">
            <SelectValue placeholder="Not set" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="true">true</SelectItem>
            <SelectItem value="false">false</SelectItem>
          </SelectContent>
        </Select>
      )
    case 'Text':
      return <Textarea rows={3} {...common} onChange={e => onChange(e.target.value)} />
    default:
      return (
        <Input
          {...common}
          className="min-w-0 flex-1"
          type={
            type === 'Int'
              ? 'number'
              : type === 'Url'
                ? 'url'
                : type === 'Secret' && !isReference
                  ? 'password'
                  : 'text'
          }
          step={type === 'Int' ? 1 : undefined}
          autoComplete={type === 'Secret' ? 'new-password' : undefined}
          onChange={e => onChange(e.target.value)}
        />
      )
  }
}

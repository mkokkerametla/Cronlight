import { useId } from 'react'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { describeRetry } from '@/lib/format'
import { draftToRetry, type RetryDraft } from '@/lib/job-form'

/** Retries for failed runs: how many, the wait before the first, and whether the wait doubles. */
export function RetryEditor({
  value,
  onChange,
}: {
  value: RetryDraft
  onChange: (value: RetryDraft) => void
}) {
  const id = useId()
  const retries = Number(value.maxRetries) || 0

  return (
    <div className="space-y-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor={`${id}-retries`}>Retries</Label>
          <Input
            id={`${id}-retries`}
            type="number"
            min={0}
            max={20}
            value={value.maxRetries}
            onChange={e => onChange({ ...value, maxRetries: e.target.value })}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor={`${id}-delay`}>Wait before retrying (seconds)</Label>
          <Input
            id={`${id}-delay`}
            type="number"
            min={1}
            max={86400}
            value={value.delaySeconds}
            disabled={retries === 0}
            required={retries > 0}
            onChange={e => onChange({ ...value, delaySeconds: e.target.value })}
          />
        </div>
      </div>
      <div className="flex items-center gap-2">
        <Checkbox
          id={`${id}-exponential`}
          checked={value.exponential}
          disabled={retries === 0}
          onCheckedChange={checked => onChange({ ...value, exponential: checked === true })}
        />
        <Label htmlFor={`${id}-exponential`} className="font-normal">
          Double the wait after each retry
        </Label>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor={`${id}-retry-on`}>Only retry these exceptions</Label>
        <Input
          id={`${id}-retry-on`}
          placeholder="Every failure, e.g. HttpRequestException, TimeoutException"
          value={value.retryOn}
          disabled={retries === 0}
          onChange={e => onChange({ ...value, retryOn: e.target.value })}
        />
      </div>
      <p className="text-xs text-muted-foreground">
        {retries === 0
          ? 'A failed run is not retried.'
          : `${describeRetry(draftToRetry(value))}. Only the last attempt's failure raises an alert.`}
      </p>
    </div>
  )
}

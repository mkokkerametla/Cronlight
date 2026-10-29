import { useEffect, useState } from 'react'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'

/**
 * Asks before a panel with unsaved changes closes by Escape, a click outside or its close button, which are easy to do
 * by mistake. The form's own Cancel button closes without asking: choosing it is the answer.
 *
 * The form reports whether it has changes with `setDirty`; the panel's Sheet takes `onOpenChange`, and renders `dialog`.
 */
export function useDiscardGuard(close: () => void, lost: string) {
  const [dirty, setDirty] = useState(false)
  const [asking, setAsking] = useState(false)

  const onOpenChange = (open: boolean) => {
    if (open) return
    if (dirty) setAsking(true)
    else close()
  }

  const dialog = (
    <AlertDialog open={asking} onOpenChange={setAsking}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Discard unsaved changes?</AlertDialogTitle>
          <AlertDialogDescription>{lost}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep editing</AlertDialogCancel>
          <AlertDialogAction
            variant="destructive"
            onClick={() => {
              setDirty(false)
              close()
            }}
          >
            Discard
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )

  return { onOpenChange, setDirty, dialog }
}

/** Tells the panel whether a form has unsaved changes, and that it has none once the form is gone */
export function useDirtyReport(dirty: boolean, onDirtyChange: (dirty: boolean) => void) {
  useEffect(() => {
    onDirtyChange(dirty)
  }, [dirty, onDirtyChange])
  useEffect(() => () => onDirtyChange(false), [onDirtyChange])
}

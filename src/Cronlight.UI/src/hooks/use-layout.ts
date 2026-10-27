import { useState } from 'react'

/** How the dashboard is laid out in this browser */
export interface Layout {
  /** Use the whole window, as by default, rather than a column of at most 80rem */
  fullWidth: boolean
  /** Tighter table rows, so more fit on screen */
  compact: boolean
}

const STORAGE_KEY = 'cronlight-layout'
const standard: Layout = { fullWidth: true, compact: false }

function stored(): Layout {
  try {
    const value = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') as Partial<Layout> | null
    // Full width unless it was turned off here
    return { fullWidth: value?.fullWidth !== false, compact: value?.compact === true }
  } catch {
    // Storage blocked, or a value from elsewhere: the standard layout
    return standard
  }
}

/** The layout chosen in this browser, remembered as the theme is */
export function useLayout(): [Layout, (change: Partial<Layout>) => void] {
  const [layout, setLayout] = useState(stored)
  const change = (next: Partial<Layout>) =>
    setLayout(current => {
      const updated = { ...current, ...next }
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(updated))
      } catch {
        // Kept for this visit only
      }
      return updated
    })
  return [layout, change]
}

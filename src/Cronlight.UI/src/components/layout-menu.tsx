import { LayoutPanelLeft } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import type { Layout } from '@/hooks/use-layout'

export function LayoutMenu({
  layout,
  onChange,
}: {
  layout: Layout
  onChange: (change: Partial<Layout>) => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="icon" aria-label="Change layout">
          <LayoutPanelLeft />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel>Layout</DropdownMenuLabel>
        <DropdownMenuCheckboxItem
          checked={layout.fullWidth}
          onCheckedChange={checked => onChange({ fullWidth: checked === true })}
        >
          Full width
        </DropdownMenuCheckboxItem>
        <DropdownMenuCheckboxItem
          checked={layout.compact}
          onCheckedChange={checked => onChange({ compact: checked === true })}
        >
          Compact rows
        </DropdownMenuCheckboxItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { ThemeProvider } from 'next-themes'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { ErrorBoundary } from '@/components/error-boundary'
import App from '@/App'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* Theme follows the OS by default; the choice is kept in localStorage per browser */}
    <ThemeProvider
      attribute="class"
      defaultTheme="system"
      enableSystem
      storageKey="cronlight-theme"
      disableTransitionOnChange
    >
      <TooltipProvider delayDuration={300}>
        <ErrorBoundary>
          <App />
        </ErrorBoundary>
        <Toaster richColors closeButton position="bottom-right" />
      </TooltipProvider>
    </ThemeProvider>
  </StrictMode>
)

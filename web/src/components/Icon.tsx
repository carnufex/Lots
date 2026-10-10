import type { ReactElement } from 'react'

/** Lots icon set (48 grid). Strokes follow currentColor; the accent is the brand teal. Sources: public/icons/*.svg. */
const PATHS = {
  insights: (
    <>
      <path d="M5 38l10-12 8 7 9-11"/><circle cx="37" cy="17" r="6" className="acc"/><path d="m41.500 21.500 4 4" className="acc"/><path d="M5 43h38"/>
    </>
  ),
  approvals: (
    <>
      <circle cx="24" cy="24" r="17"/><path d="m16 24.500 6 6 11-12" className="acc"/><path d="M24 7V3M24 45v-4"/>
    </>
  ),
  audit: (
    <>
      <rect x="8" y="6" width="32" height="36" rx="4"/><circle cx="16" cy="16" r="2" className="acc fill"/><path d="M22 16h12M16 24h18M16 32h12"/><path d="M32 32h2" className="acc"/>
    </>
  ),
  identity: (
    <>
      <circle cx="21" cy="15" r="7"/><path d="M7 41c0-8 6-13 14-13 3 0 5.500.7 7.500 1.800"/><polygon points="35.00,27.00 41.93,31.00 41.93,39.00 35.00,43.00 28.07,39.00 28.07,31.00" className="acc" fill="none"/><path d="m31.500 35 2.500 2.500 4-5" className="acc"/>
    </>
  ),
  mcp: (
    <>
      <polygon points="24.00,17.00 30.06,20.50 30.06,27.50 24.00,31.00 17.94,27.50 17.94,20.50" className="acc" fill="none"/><path d="M19 19 12 14M29 19l7-5M24 31v6"/><circle cx="9" cy="12" r="4"/><circle cx="39" cy="12" r="4"/><circle cx="24" cy="41" r="4"/>
    </>
  ),
  models: (
    <>
      <rect x="11" y="11" width="26" height="26" rx="4"/><path d="M18 5v6M30 5v6M18 37v6M30 37v6M5 18h6M5 30h6M37 18h6M37 30h6"/><polygon points="24.00,18.00 29.20,21.00 29.20,27.00 24.00,30.00 18.80,27.00 18.80,21.00" className="acc" fill="none"/>
    </>
  ),
  policy: (
    <>
      <path d="M24 5 9 11v12c0 10 6.500 17 15 20 8.500-3 15-10 15-20V11z"/><path d="m17 24 5 5 9-10" className="acc"/>
    </>
  ),
  profiles: (
    <>
      <polygon points="15.00,9.00 21.93,13.00 21.93,21.00 15.00,25.00 8.07,21.00 8.07,13.00" fill="none"/><polygon points="31.00,9.00 37.93,13.00 37.93,21.00 31.00,25.00 24.07,21.00 24.07,13.00" fill="none"/><polygon points="23.00,23.00 29.93,27.00 29.93,35.00 23.00,39.00 16.07,35.00 16.07,27.00" className="acc" fill="none"/>
    </>
  ),
  rag: (
    <>
      <path d="M12 6h16l8 8v8M12 6v34h12"/><path d="M28 6v8h8M18 20h10M18 27h6"/><circle cx="34" cy="34" r="7" className="acc"/><path d="M39.5 39.5 44 44" className="acc"/>
    </>
  ),
  chat: (
    <>
      <path d="M8 12a4 4 0 0 1 4-4h24a4 4 0 0 1 4 4v16a4 4 0 0 1-4 4H20l-8 7v-7h0a4 4 0 0 1-4-4z"/><path d="M16 18h16M16 24h10" className="acc"/>
    </>
  ),
  runs: (
    <>
      <circle cx="24" cy="24" r="17"/><path d="M20 16.5v15l12-7.5z" className="acc"/>
    </>
  ),
  tools: (
    <>
      <path d="M16 6c-5 0-6 2-6 6v6c0 3-2 5-5 6 3 1 5 3 5 6v6c0 4 1 6 6 6M32 6c5 0 6 2 6 6v6c0 3 2 5 5 6-3 1-5 3-5 6v6c0 4-1 6-6 6"/><polygon points="24.00,17.00 30.06,20.50 30.06,27.50 24.00,31.00 17.94,27.50 17.94,20.50" className="acc"/>
    </>
  ),
  transcribe: (
    <>
      <path d="M9 18v12M14 12v24M19 19v10" className="acc"/><path d="M27 15h16M27 24h16M27 33h10"/>
    </>
  ),
  voice: (
    <>
      <path d="M6 19h7l9-8v26l-9-8H6z"/><path d="M29 18c3 3.5 3 8.500 0 12M35 12c6 6.500 6 17.500 0 24" className="acc"/>
    </>
  ),
} satisfies Record<string, ReactElement>

export type IconName = keyof typeof PATHS

export function Icon({ name, size = 20 }: { name: IconName; size?: number }) {
  return (
    <svg
      className="icon"
      viewBox="0 0 48 48"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {PATHS[name]}
    </svg>
  )
}

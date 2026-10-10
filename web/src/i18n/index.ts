import { sv } from './sv'

/**
 * UI language (#111). The English text is the key: t('New chat') looks up the current language's catalogue and falls back to the
 * English text, so a missing translation shows English, never a key. Catalogues are plain resource files (sv.ts); English needs
 * none. The app re-renders from the top when the language changes, so a module-level t() is enough.
 */
export type UiLanguage = 'en' | 'sv'

const CATALOGUES: Record<UiLanguage, Record<string, string>> = { en: {}, sv }
const KEY = 'lots.ui-language'

function initial(): UiLanguage {
  try {
    const saved = localStorage.getItem(KEY)
    if (saved === 'en' || saved === 'sv') return saved
  } catch {
    /* ignore */
  }
  return navigator.language.toLowerCase().startsWith('sv') ? 'sv' : 'en'
}

let current: UiLanguage = initial()

export function language(): UiLanguage {
  return current
}

export function setLanguage(l: UiLanguage) {
  current = l
  try {
    localStorage.setItem(KEY, l)
  } catch {
    /* a per-viewer convenience only */
  }
  document.documentElement.lang = l
}

document.documentElement.lang = current

/** Translates; {name} placeholders are filled from vars. */
export function t(text: string, vars?: Record<string, string | number>): string {
  let s = CATALOGUES[current][text] ?? text
  if (vars) for (const [k, v] of Object.entries(vars)) s = s.replaceAll(`{${k}}`, String(v))
  return s
}

const LOCALE: Record<UiLanguage, string> = { en: 'en-GB', sv: 'sv-SE' }

/** Dates, times and numbers in the UI language's conventions. */
export const fmt = {
  dateTime: (iso: string | number | Date) => new Date(iso).toLocaleString(LOCALE[current]),
  date: (iso: string | number | Date) => new Date(iso).toLocaleDateString(LOCALE[current]),
  time: (iso: string | number | Date) => new Date(iso).toLocaleTimeString(LOCALE[current]),
  number: (n: number, digits?: number) =>
    n.toLocaleString(LOCALE[current], digits === undefined ? undefined : { minimumFractionDigits: digits, maximumFractionDigits: digits }),
}

const STATUS: Record<string, string> = {
  Pending: 'Pending',
  Running: 'Running',
  WaitingForApproval: 'Waiting for approval',
  Completed: 'Completed',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
}

/** A run or approval status as shown to people (the API value stays the CSS class). */
export function statusText(status: string): string {
  return t(STATUS[status] ?? status)
}

/**
 * Theme (#155): light, dark or the system's. The resolved theme is always written to <html data-theme>, so CSS only has two
 * cases. theme-init.js applies the saved choice before the first paint; this module keeps it in sync afterwards.
 */
export type Theme = 'system' | 'light' | 'dark'

const KEY = 'lots.theme'
const media = () => window.matchMedia?.('(prefers-color-scheme: light)')

export function theme(): Theme {
  try {
    const t = localStorage.getItem(KEY)
    if (t === 'light' || t === 'dark' || t === 'system') return t
  } catch {
    /* ignore */
  }
  return 'system'
}

function resolve(t: Theme): 'light' | 'dark' {
  return t === 'system' ? (media()?.matches ? 'light' : 'dark') : t
}

function paint(t: Theme) {
  const r = resolve(t)
  document.documentElement.dataset.theme = r
  document.documentElement.style.colorScheme = r
}

/** Switches without a reload; the server copy is saved by the caller (preferences.ts). */
export function setTheme(t: Theme) {
  try {
    localStorage.setItem(KEY, t)
  } catch {
    /* a per-device fallback only */
  }
  paint(t)
}

// Follow the system while the choice is "system".
media()?.addEventListener('change', () => {
  if (theme() === 'system') paint('system')
})
paint(theme())

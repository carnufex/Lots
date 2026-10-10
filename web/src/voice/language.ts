import type { VoiceLanguage } from '../api'

export const LANGUAGE_KEY = 'lots.voice-language'

/** Remembered choice, else the installation's default, else the browser language (a hint beats detecting it from a short clip). */
export function initialLanguage(configured: VoiceLanguage): VoiceLanguage {
  try {
    const saved = localStorage.getItem(LANGUAGE_KEY)
    if (saved === 'sv' || saved === 'en' || saved === 'auto') return saved
  } catch {
    /* ignore */
  }
  if (configured !== 'auto') return configured
  const l = navigator.language.toLowerCase()
  return l.startsWith('sv') ? 'sv' : l.startsWith('en') ? 'en' : 'auto'
}

export function saveLanguage(l: VoiceLanguage) {
  try {
    localStorage.setItem(LANGUAGE_KEY, l)
  } catch {
    /* a per-viewer convenience only */
  }
}

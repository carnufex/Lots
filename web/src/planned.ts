import type { IconName } from './components/Icon'

export type Planned = { slug: string; label: string; icon: IconName; blurb: string }

/** Menu entries whose pages are not built yet: the shell shows them so the structure is visible. */
export const PLANNED: Planned[] = [
  { slug: 'transcription', label: 'Transcription', icon: 'transcribe', blurb: 'Speech to text: dictation vocabulary, languages and transcription of recordings.' },
]

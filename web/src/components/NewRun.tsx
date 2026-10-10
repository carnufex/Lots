import { useState } from 'react'
import type { Api, VoiceLanguage } from '../api'
import type { ProfileInfo, VoiceConfig } from '../config'
import MicButton from './MicButton'
import VocabularyEditor from './VocabularyEditor'
import { initialLanguage, saveLanguage } from '../voice/language'
import { AttachPicker, type UploadedAttachment } from './Attachments'
import { t } from '../i18n'
import { defaultContext } from '../preferences'

export default function NewRun({ api, profiles, voice }: { api: Api; profiles: ProfileInfo[]; voice: VoiceConfig }) {
  const [prompt, setPrompt] = useState('')
  const [files, setFiles] = useState<UploadedAttachment[]>([])
  const [profile, setProfile] = useState(() => defaultContext(profiles))
  const [busy, setBusy] = useState(false)
  const [language, setLanguageState] = useState<VoiceLanguage>(() => initialLanguage(voice.defaultLanguage))
  const setLanguage = (l: VoiceLanguage) => {
    setLanguageState(l)
    saveLanguage(l)
  }
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!prompt.trim() || busy) return
    setBusy(true)
    setError(null)
    try {
      const run = await api.startRun(prompt.trim(), profile, { attachments: files.map((f) => f.id) })
      window.location.hash = `#/runs/${run.id}`
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <form className="newrun" onSubmit={(e) => void submit(e)}>
      <label className="sr" htmlFor="prompt">
        {t('Question')}
      </label>
      <textarea
        id="prompt"
        rows={2}
        placeholder={t('Ask something, e.g. which containers are unhealthy?')}
        value={prompt}
        onChange={(e) => setPrompt(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void submit(e)
        }}
      />
      <div className="row">
        {profiles.length > 1 ? (
          <select aria-label={t('Profile')} value={profile} onChange={(e) => setProfile(e.target.value)}>
            {profiles.map((p) => (
              <option key={p.name} value={p.name}>
                {p.name}
              </option>
            ))}
          </select>
        ) : (
          <span className="muted">{profile}</span>
        )}
        <AttachPicker api={api} files={files} onChange={setFiles} disabled={busy} />
        <span className="grow" />
        {voice.enabled && (
          <>
            <select aria-label={t('Speech language')} value={language} onChange={(e) => setLanguage(e.target.value as VoiceLanguage)}>
              <option value="auto">{t('Auto')}</option>
              <option value="sv">Svenska</option>
              <option value="en">English</option>
            </select>
            <MicButton api={api} language={language} onText={(text) => setPrompt((p) => (p.trim() ? `${p.trim()} ${text}` : text))} />
          </>
        )}
        {error && (
          <span role="alert" className="error">
            {error}
          </span>
        )}
        <button className="btn primary" type="submit" disabled={busy || !prompt.trim()}>
          {busy ? t('Starting…') : t('Start run')}
        </button>
      </div>
      {voice.enabled && <VocabularyEditor api={api} />}
    </form>
  )
}

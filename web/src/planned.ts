import type { IconName } from './components/Icon'

export type Planned = { slug: string; label: string; icon: IconName; blurb: string }

/** Menu entries whose pages are not built yet: the shell shows them so the structure is visible. */
export const PLANNED: Planned[] = [
  { slug: 'knowledge', label: 'Knowledge', icon: 'rag', blurb: 'Bring your own documents and sources for retrieval (RAG). You choose what is indexed and who may search it.' },
  { slug: 'tools', label: 'Tools', icon: 'tools', blurb: 'Every tool the agent can call, with its risk class, and which roles may use it.' },
  { slug: 'mcp', label: 'MCP servers', icon: 'mcp', blurb: 'Connect MCP servers and choose which of their tools are exposed to which profile.' },
  { slug: 'transcription', label: 'Transcription', icon: 'transcribe', blurb: 'Speech to text: dictation vocabulary, languages and transcription of recordings.' },
  { slug: 'models', label: 'Models', icon: 'models', blurb: 'Model endpoints (any OpenAI-compatible API) and which profile uses which.' },
  { slug: 'profiles', label: 'Profiles', icon: 'profiles', blurb: 'The domains the shell serves: instructions, tools, roles and approval rules, as config as code.' },
  { slug: 'policy', label: 'Policy', icon: 'policy', blurb: 'Role grants and approval rules, evaluated outside the model on every tool call.' },
  { slug: 'identity', label: 'Identity', icon: 'identity', blurb: "Login, role mapping and how tools are called on the user's behalf." },
]

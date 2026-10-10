import type { Api } from '../api'
import { CatalogTab, ServersTab } from './ServersTab'
import ToolCallsTab from './ToolCallsTab'
import CredentialsTab from './CredentialsTab'
import { t } from '../i18n'

export const INTEGRATION_TABS = [
  { slug: 'tool-calls', label: t('Tool calls'), blurb: t('Every tool call across runs: who, which tool, arguments, result, latency and the policy decision.') },
  { slug: 'mcp', label: t('MCP servers'), blurb: t('Connected MCP servers: transport, authentication, health and the tools each one exposes.') },
  { slug: 'catalog', label: t('Catalog'), blurb: t('All tools with their risk class and which profiles and roles may use them. New tools stay blocked until classified.') },
  { slug: 'credentials', label: t('Credentials'), blurb: t('Secret references and per-user connections for integrations. Values are never stored here.') },
] as const

export type IntegrationTab = (typeof INTEGRATION_TABS)[number]['slug']

/** One place for everything the agent can call: tool calls, servers, the catalog and credentials. */
export default function IntegrationsPage({ api, tab }: { api: Api; tab: IntegrationTab }) {
  const current = INTEGRATION_TABS.find((it) => it.slug === tab) ?? INTEGRATION_TABS[0]
  return (
    <section>
      <h1>{t('Integrations')}</h1>
      <div className="tabs" role="tablist" aria-label={t('Integrations')}>
        {INTEGRATION_TABS.map((it) => (
          <a key={it.slug} href={`#/integrations/${it.slug}`} role="tab" aria-selected={it.slug === current.slug} className={it.slug === current.slug ? 'tab active' : 'tab'}>
            {it.label}
          </a>
        ))}
      </div>
      {current.slug === 'tool-calls' ? (
        <ToolCallsTab api={api} />
      ) : current.slug === 'mcp' ? (
        <ServersTab api={api} />
      ) : current.slug === 'catalog' ? (
        <CatalogTab api={api} />
      ) : (
        <CredentialsTab api={api} />
      )}
      <p className="muted small">{current.blurb}</p>
    </section>
  )
}

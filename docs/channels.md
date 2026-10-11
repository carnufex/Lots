# Channels: Slack, Teams, e-mail and the OpenAI-compatible API

People can ask Lots where they already work (#107). Every channel run is the asking person's own Lots identity, with the roles of
their latest login through the identity provider: policy, approvals, quotas and audit are the same as in the web UI.

## Identity mapping

A Slack user or a mail sender is mapped by **e-mail address** to the Lots user who logged in with that address within
`Channels:IdentityMaxAgeDays` (90). No match, or more than one, means no run: Lots never guesses. People therefore log in to the
web UI once before they use a channel.

Delegated backends (ADR 0011) need the person's own login token, which a channel message does not carry. Such tool calls fail
closed from channels; servers that use a service account or per-user connections work as usual.

## Slack

Create a Slack app with a bot user, the `app_mentions:read`, `im:history`, `chat:write` and `users:read.email` scopes, event
subscriptions for `app_mention` and `message.im` at `https://<lots>/channels/slack/events`, and interactivity at
`https://<lots>/channels/slack/interactive`. Then:

```yaml
Channels:
  Slack:
    Enabled: true
    SigningSecretRef: env:SLACK_SIGNING_SECRET   # every request is verified (HMAC, at most 5 minutes old)
    BotTokenRef: env:SLACK_BOT_TOKEN
    Profile: homelab
```

Mention the app or send it a direct message; the answer arrives in the thread. When a run needs an approval, the thread gets the
request with **Approve** and **Deny** buttons. A click is decided as the clicking person's Lots identity through exactly the same
rules as the web UI (roles, two-person rule, never the requester); requests that need a written reason point to the web UI. Typing
"yes" in the thread never approves anything. Each Slack event is handled once (retries and duplicates are dropped).

## E-mail to run

Lots does not read a mailbox itself: a mail relay (Mailgun or SendGrid inbound parse, or a small script on the mail server) posts
each message to `POST /channels/email/inbound` with `Authorization: Bearer <secret>`:

```json
{ "from": "Alice <alice@example.org>", "subject": "Backups", "text": "Did last night's backup run?", "messageId": "<id@mail>", "verified": true }
```

`verified` must be true (the relay checked SPF/DKIM): a From header alone proves nothing. Unverified mail, unknown senders and
duplicates are accepted and ignored without a reply, so spoofed mail cannot make Lots send mail. The answer goes back by mail
through the configured SMTP server (`Notifications:Email`). Approvals cannot be given by mail.

```yaml
Channels:
  Email: { Enabled: true, SecretRef: env:LOTS_MAIL_RELAY_SECRET, Profile: homelab }
```

## OpenAI-compatible API

Other tools can use Lots as a model: `POST /v1/chat/completions` (streaming with `stream: true`) and `GET /v1/models`, with an
OIDC access token or a personal API token with the `runs` scope.

- `model` is a profile name (`homelab` or `lots:homelab`); `/v1/models` lists the profiles your roles can use.
- The last user message is the question; earlier messages are given as context. A client's system message is context, never the
  system prompt, and client-side tools are ignored: Lots uses its own tools under its own policy.
- A run that needs an approval answers with a link to it; a run still working after `Channels:OpenAi:WaitSeconds` (300) answers
  with a link to follow it.

```bash
curl -s https://lots.example.org/v1/chat/completions -H "Authorization: Bearer $LOTS_TOKEN" -H "Content-Type: application/json" \
  -d '{"model":"homelab","messages":[{"role":"user","content":"Which containers are unhealthy?"}]}'
```

## Microsoft Teams

Teams talks to bots through the Bot Framework (#149); outgoing webhooks cannot answer later, so Lots is a bot:

1. Create an **Azure Bot** resource (free tier is enough) with a Microsoft app id and a client secret, single-tenant in your Entra
   tenant or multi-tenant. Set its messaging endpoint to `https://<lots>/channels/teams/messages` and enable the Teams channel.
2. Put the client secret in your secret store and configure Lots:

```yaml
Channels:
  Teams:
    Enabled: true
    AppId: 00000000-0000-0000-0000-000000000000
    AppPasswordRef: env:TEAMS_BOT_SECRET   # or file:/run/secrets/teams-bot
    TenantId: <tenant id>                  # single-tenant bots; leave out for multi-tenant
    Profile: homelab
    MatchObjectId: false                   # true only when Entra is the IdP and Auth:Oidc:UserClaim is oid
```

3. Add the bot to Teams with an app manifest (Developer Portal) that names the bot id, then mention it in a channel or chat with it.

Every activity carries a JWT from the Bot Framework. Lots checks the signature against the Bot Framework's published keys (and the
key's channel endorsements), the issuer, that the audience is this bot, the expiry, and that the token's `serviceurl` is the one the
activity names. Replies go only to `Channels:Teams:ServiceUrls` (default `https://smba.trafficmanager.net/`): an activity naming
any other host is refused, so the bot's token never leaves for a host Microsoft did not vouch for.

People are mapped by the e-mail or user principal name in the conversation roster, exactly like Slack users (log in to Lots once with
the same address). With Entra as the identity provider and `oid` as the user claim, `MatchObjectId: true` maps `from.aadObjectId`
directly. When a run needs an approval the conversation gets an Adaptive Card with **Approve**, **Deny** (`Action.Execute`) and a link
to Lots; a click is decided as the clicking person through the same rules as the web UI. Typing "yes" never approves anything.
Each message is handled once (redeliveries are dropped).

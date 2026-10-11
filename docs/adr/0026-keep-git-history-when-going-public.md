# 0026. Keep the git history when the repository goes public

Status: accepted (2026-10-11). Decided in #160, part of #131.

## Context

Before the repository is made public, the working tree was scrubbed of real hostnames and LAN addresses, and `scripts/check-public.sh`
(in `scripts/ci.sh`) keeps it that way: gitleaks over tree and history, plus a grep for the private patterns in the git-ignored
`.public-patterns`. A gitleaks scan of the whole history found no real secrets; its only hits are synthetic tokens in the secret
detector's own tests (`.gitleaksignore`).

The older commits still contain the owner's own hostnames (registry, production URL, CMDB, IdP) about 300 times, and one LAN address of
the GPU host. `.env`, `profiles.local/` and voice recordings were never committed.

The options were to keep the history, to rewrite it with `git filter-repo` and force-push, or to publish a fresh repository with one
squashed commit and keep this one private.

## Decision

Keep the history and publish the repository as it is.

The hostnames are already discoverable: their certificates are in the Certificate Transparency logs and the public ones resolve in DNS.
The LAN address is reachable only inside the LAN. Rewriting would change every commit SHA: the `sha-<commit>` tags of the images running
in production, the commit references in issues and the changelog would stop matching, and every clone would have to be re-cloned. That
loss of traceability (principle 5) is worth more than hiding low-sensitivity names.

## Consequences

- Anyone can read the old hostnames in old commits. Nothing that grants access (secrets, tokens, keys) is in the history, and
  `scripts/check-public.sh` keeps it that way for new commits.
- Commit SHAs, image tags and issue references stay valid.
- New private details keep going to the homelab repository, `profiles.local/` and the secret store, never into this repository.
- Making the repository public is a GitHub visibility switch for the owner; nothing in the code blocks it any more.

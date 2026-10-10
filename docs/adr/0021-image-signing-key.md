# 0021. Image signing with a cosign key pair kept in the secret store

Status: accepted (2026-10-10). Decides #148.

## Context

#91 added image signing with cosign, but signing needs a key, and where that key lives is a custody decision. Keyless signing
(Fulcio/Rekor) publishes every digest to a public log and needs an interactive login or GitHub Actions OIDC, which is unavailable.
Vault transit would keep the key inside Vault, but the reference deployment does not run Vault.

## Decision

1. Sign with a **cosign key pair**. The private key (base64-encoded) and its password are kept in the operator's secret store
   (Bitwarden Secrets Manager, homelab project: `cosign-key`, `cosign-password`). The public key is committed as `cosign.pub`.
2. Sign by digest, without a transparency log (`--tlog-upload=false`): the registry is private. The signature is a tagged manifest
   next to the image, so registry garbage collection keeps it.
3. `scripts/supply-chain.sh sign` takes the key as a file (`COSIGN_KEY`) or as the stored base64 (`COSIGN_KEY_B64`), decoded only
   into a temporary file while signing. `scripts/release.sh --push` signs when either is set.
4. Enforcing signatures in a cluster is a separate, explicit change in the deployment repository.

## Consequences

- Anyone with `cosign.pub` (and pull access) can verify an image: `scripts/supply-chain.sh verify <ref@digest>`.
- Whoever holds the secret store's access token can sign. Rotating means a new key pair, a new `cosign.pub` and re-signing the
  images still in use.
- The production shell image running at the time of the decision is signed and verified.

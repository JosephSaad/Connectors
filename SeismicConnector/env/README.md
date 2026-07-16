# env/

Environment layering (lowest to highest precedence):

1. `env/.env.local` — non-secret configuration, may differ per node.
2. `env/.env.local.user` — every `SECRET_*` value. Never committed.
3. The real process environment — always wins (container / Windows-service env blocks).

Fallbacks `./.env.local` and `./.env.local.user` in the repo root are honoured
when the `env/` copies do not exist.

Start from `.env.local.example`, which documents every knob and its default.
With `USE_KEY_VAULT=true` the `SECRET_*` values can live in Azure Key Vault
instead of `.env.local.user` (secret name = env var lowercased, `_` → `-`).

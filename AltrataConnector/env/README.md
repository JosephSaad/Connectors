# env/ — configuration layering

| File | Purpose | Committed? |
|---|---|---|
| `.env.local.example` | Documented template for every knob | yes |
| `.env.local` | Non-secret config for this node | **never** |
| `.env.local.user` | `SECRET_*` values only | **never** |

Load order (first value wins per key):

1. Real process environment (container / Windows-service env block)
2. `env/.env.local`
3. `env/.env.local.user`
4. `./.env.local`, `./.env.local.user` (repo-root fallbacks)

With `USE_KEY_VAULT=true`, any `SECRET_*` value missing from the environment is
fetched from Azure Key Vault (`KEY_VAULT_URI`) under the name
`secret-...` (lowercased, `_` → `-`).

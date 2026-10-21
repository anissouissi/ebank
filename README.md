# Ebank

Ebank is a virtual-banking application foundation: React + TypeScript for the
web client, ASP.NET Core for APIs, and Keycloak for customer identity.

> The dashboard is a UI demo. Its balances and transactions are illustrative,
> not connected to a banking system. The AI assistant is read-only and real
> financial operations are not implemented.

## Local development

Requirements: .NET 10 SDK, Node.js/npm, and Docker Compose.

1. Set local Keycloak admin and database credentials, generate a stable
   encryption key for KYC documents, and start PostgreSQL and Keycloak:

   ```sh
   cp -n .env.example .env
   # Replace all sample passwords with long local-only passwords.
   # Generate a document key with: openssl rand -base64 32
   # Save that output as KYC_DOCUMENT_ENCRYPTION_KEY in .env. Keep it safe.
   # If .env already exists, preserve it and add any missing settings from
   # .env.example rather than replacing it.
   docker compose up -d
   ```

   PostgreSQL stores Keycloak's realm and identity data in the persistent
   `keycloak-db` Docker volume. Keycloak imports the `ebank` realm and
   `ebank-web` public client on first start. The Compose stack starts the
   authenticated MCP service. Start LM Studio separately, enable its local
   server at `http://127.0.0.1:1234`, and load `google/gemma-4-26b-a4b-qat`
   before starting the API. Self-registration is enabled:
   open <http://localhost:5173> and choose **Create an account** to use
   Keycloak's hosted registration page, then sign in with the new credentials.
   The admin console is at
   <http://localhost:8080>.
   The separate `ebank-db` service stores customer KYC profile, document, and
   demo account records in its own `ebank` database and persistent `ebank-db`
   volume, exposed to the host on port 5433.

   If LM Studio requires an API key, store it as a local user secret with
   `dotnet user-secrets set "Assistant:ApiKey" "<your-local-key>" --project src/Ebank.Api`.
   Realm import happens only when the realm does not already exist. If you started
   Keycloak before enabling registration or adding the API/MCP audiences,
   update that existing realm in the admin console: enable **Realm settings →
   Login → User registration**, and ensure the `ebank-api` bearer-only client
   and `ebank-mcp` bearer-only clients exist. Under **Clients → ebank-web →
   Client scopes → ebank-web-dedicated**, add audience mappers for both
   `ebank-api` and `ebank-mcp`, with **Add to access token** enabled. Sign out
   and sign in again after changing these settings. Do not remove the database
   volume to apply settings unless you intend to erase all local Keycloak users
   and data. The MCP container uses Keycloak's internal Compose address for
   signing-key retrieval while continuing to validate the browser-facing
   `http://localhost:8080/realms/ebank` token issuer.

2. Configure the API's local secrets once, apply the database migration, and
   start the API:

   ```sh
   set -a
   . ./.env
   set +a
   dotnet user-secrets set "Kyc:DocumentEncryptionKey" "$KYC_DOCUMENT_ENCRYPTION_KEY" --project src/Ebank.Api
   dotnet user-secrets set "ConnectionStrings:Ebank" "Host=localhost;Port=5433;Database=ebank;Username=$EBANK_DB_USER;Password=$EBANK_DB_PASSWORD" --project src/Ebank.Api
   dotnet tool restore
   dotnet ef database update --project src/Ebank.Api
   dotnet run --project src/Ebank.Api --urls http://0.0.0.0:5100
   ```

   The local MCP container calls the host-run API through
   `host.docker.internal:5100`; binding to `0.0.0.0` allows that connection.
   This exposes the development API on your machine's network interfaces, so
   use only on a trusted local network and stop it when not developing.
   `dotnet user-secrets` stores these values outside the repository and makes
   them available to both `dotnet run` and IDE launches. If you already started
   the API, stop and restart it after setting the secrets. Keep the encryption
   key stable across restarts and backups; changing or losing it makes
   already-uploaded documents unreadable. Treat it as a secret and do not
   commit it. `GET /api/health` is public; `/api/me` and all `/api/kyc`
   endpoints require a Keycloak access token.
   The API preserves JWT claim names so it can scope KYC records to Keycloak's
   `sub` claim. Restart the API after changing authentication configuration.
   The token must contain `ebank-api` in its `aud` claim; after changing the
   Keycloak audience/client configuration, sign out and sign in again so the
   browser obtains a new access token. The authenticated `/api/accounts`
   endpoints list, create, and retrieve the current customer's demo accounts.
   Account creation returns a zero-balance USD demo record and never creates a
   real bank account or enables payments. `/api/transactions` returns
   customer-scoped transaction history with optional account, date/time,
   search, and pagination filters. Date ranges use an inclusive `from` and
   exclusive `to` timestamp. `POST /api/transactions/demo` records clearly
   illustrative e-commerce spending, ATM withdrawals, salary, or transfers.
   These entries update demo balances; transfers create paired entries and
   debits are rejected if funds are insufficient. `/api/overview` aggregates
   the signed-in customer's account balances, current-UTC-month income and
   spending, and latest recorded transactions from PostgreSQL. Apply schema
   changes with `dotnet ef database update --project src/Ebank.Api`.

   In the supplied local database, account
   `a94ebf7b-3239-4c16-a0cc-5f43360c17a1` is the current account and
   `2ac18289-65f3-47be-980d-e9bd7c363e8d` is the savings account. To populate
   the current account with 206 transactions and the savings account with 117
   transactions across the prior 90 days, first apply the database migrations,
   then run the idempotent seed script. It updates each account balance by the
   net value of newly inserted activity. Run the
   command from the repository root; `docker compose exec -T ebank-db` runs
   `psql` inside the database container, and the script is streamed through
   standard input:

   ```sh
   docker compose exec -T ebank-db sh -c \
     'PGPASSWORD="$POSTGRES_PASSWORD" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
     < src/Ebank.Api/SeedData/demo-transactions.sql
   ```

3. Start the React client in another terminal:

   ```sh
   cd web
   npm install
   npm run dev
   ```

   Open <http://localhost:5173> to create an account or sign in.
   The Overview page reads account and transaction summaries from PostgreSQL.
   **Accounts** in the sidebar lists or creates current and savings demo
   accounts. **Transactions** shows history and filters by account, date range,
   or transaction name/label. **Demo** creates sample transaction activity
   against your own accounts. Use **Profile** in the avatar menu to save
   contact/address details and upload an identity document plus a recent bill.
   Vite proxies `/api` requests to the local ASP.NET Core API on port 5100.
   After pulling or building API changes, stop any previously running API
   process and restart it so the running process loads the new routes.

   The **Ask Ebank assistant** panel is available from the signed-out welcome
   page and dashboard. It requires an authenticated Keycloak session before
   sending a message; the browser refreshes the access token and sends it only
   to the protected `/api/assistant/chat` endpoint. The API validates the
   session and passes the token to the separately hosted MCP service, which
   validates it again and forwards it to the existing customer-scoped APIs.
   The token is never included in prompts, tool arguments, conversation
   history, or requests to the model provider. The assistant uses the configured
   Gemma model in LM Studio by default and provides five read-only tools:
   `list_accounts`, `get_recent_transactions`,
   `get_transactions_in_date_range`, `get_account_recent_transactions`, and
   `get_account_transactions_in_date_range`. Date-range tool dates are UTC
   calendar dates, inclusive at both ends. Range results are capped at 1,000
   entries; the assistant reports when this safety cap truncates a result.
   The API requests low reasoning effort so the model can select and call
   the appropriate MCP tool, limits responses to 4,096 tokens, and allows up
   to three minutes for the LM Studio HTTP request. Configure the model's
   context size in LM Studio.
   Reasoning content is separate from the assistant's user-facing text. The API
   logs MCP connection, tool discovery, and model/tool
   run durations separately to help identify future bottlenecks. The Agent
   Framework agent receives all five authenticated MCP tools and selects the
   appropriate tool based on the user's request; the API does not route requests
   by matching phrases or call individual tools outside the agent run.
   The assistant's **Debug mode** checkbox switches the authenticated chat to a
   Server-Sent Events stream. Model reasoning, selected tool calls and arguments,
   tool results, and answer text appear as the agent emits them. Tool results can
   contain account and transaction data; leave debug mode off during normal use
   and avoid sharing its output.
   Long assistant replies remain visible in the chat but are summarized when
   sending follow-up context; this keeps large transaction lists from causing
   request-validation errors or needlessly expanding model prompts.
   The local MCP host port is bound to `127.0.0.1:7101`; the assistant API
   reaches LM Studio at `127.0.0.1:1234` and MCP at `localhost:7101`.

## Configuration

The development API reads Keycloak's local issuer from
`src/Ebank.Api/appsettings.Development.json`. Production deployments must
provide an HTTPS `Keycloak:Authority`, the expected `Keycloak:Audience`, an
explicit `Cors:AllowedOrigins` list, and keep `Keycloak:RequireHttpsMetadata`
enabled. The React client supports `VITE_KEYCLOAK_URL`,
`VITE_KEYCLOAK_REALM`, and `VITE_KEYCLOAK_CLIENT_ID` overrides.

The browser uses OIDC Authorization Code with PKCE; it has no client secret.
The API independently validates bearer tokens and protects customer data.
Development Keycloak uses `start-dev` and HTTP: do not use this setup or its
realm settings as a production deployment.

Self-registration creates a Keycloak identity only. The application supports
illustrative demo account and transaction records, but does not create real
bank accounts, maintain a production ledger, or process payments. Demo
transactions are not real banking or payment operations. The application also
does not complete KYC/identity verification. Those banking and regulatory
onboarding flows still need to be implemented before this can be used as a
banking product.

Uploaded PDF/PNG/JPEG documents are limited to 5 MB, checked against their file
signature, encrypted by the API with AES-256-GCM, and stored as ciphertext in
PostgreSQL. The API records the authenticated Keycloak subject as the owner and
returns only document metadata, never file contents. A submission with details
and both documents is marked `PendingReview`; no OCR, bill matching, identity
verification, phone OTP verification, staff review UI, or approval workflow is
implemented. The customer profile fields themselves are stored in the Ebank
database and are not application-level encrypted. Use appropriately encrypted
database storage, secure backups, TLS, access controls, retention/deletion
policies, malware scanning, and a managed secrets/key-rotation process before
handling real customer data.

## Next implementation steps

1. Add phone verification and a staff-only KYC review/approval workflow.
2. Replace demo account and transaction records with a ledger only after the
   necessary banking controls and regulatory requirements are defined.
3. Add tests, observability, and threat-model-driven evaluation before
   considering any customer-confirmed write actions.

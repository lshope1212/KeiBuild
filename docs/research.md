# HA4 research engine

**KeiBuild.Research** is a .NET 10 console-hosted application. It has its own PostgreSQL connection and provider credentials. Run it independently of the Blazor project.

## Set up PostgreSQL

From the solution folder in PowerShell, start the local PostgreSQL 17 service with Docker Compose. Enter a local development password at the prompt.

```powershell
$env:KEIBUILD_POSTGRES_PASSWORD = ([System.Net.NetworkCredential]::new('', (Read-Host 'PostgreSQL password' -AsSecureString))).Password
docker compose up -d --wait research-db
$env:KEIBUILD_RESEARCH_DB = 'Host=127.0.0.1;Port=5444;Database=keibuild;Username=keibuild;Password="{0}"' -f $env:KEIBUILD_POSTGRES_PASSWORD.Replace('"', '""')
```

For a PostgreSQL server you already run, create a dedicated development database and a login with permission to create schemas and tables in it. Set `KEIBUILD_RESEARCH_DB` to its Npgsql connection string and use your server’s TLS settings for remote connections. The Compose service binds to localhost. Its named volume persists data across restarts. [PostgreSQL installers](https://www.postgresql.org/download/) · [Npgsql connection strings](https://www.npgsql.org/doc/connection-string-parameters.html)

Apply the checked-in EF Core 10 migration.

```powershell
dotnet restore .\KeiBuild.slnx
dotnet run --project .\KeiBuild.Research -- migrate
```

For migration development, use `dotnet-ef` 10.0.12. The design-time factory uses the same connection environment variable. An example for checking schema drift follows.

```powershell
dotnet ef migrations has-pending-model-changes --project .\KeiBuild.Research
```

## Run the offline campaign

The checked-in fixture documents, search results, and AI responses are synthetic. The demo applies migrations, starts a campaign if needed, and completes a bounded batch.

```powershell
dotnet run --project .\KeiBuild.Research -- demo ha4-demo
dotnet run --project .\KeiBuild.Research -- status ha4-demo
dotnet run --project .\KeiBuild.Research -- sources ha4-demo
dotnet run --project .\KeiBuild.Research -- claims ha4-demo
dotnet run --project .\KeiBuild.Research -- resume ha4-demo --search-limit 0
```

On a fresh campaign, the demo searches 14 queries covering all seven topic families. It processes five document versions, including a mirror that reuses the first document’s analysis. It stores eight claims, flags two conflicting values, and withholds an unsupported value. It also leaves an unapproved source blocked. Resuming with `--search-limit 0` checks the work queue without starting more searches. A completed version is skipped unless you request a refresh.

To preview the 28 deterministic English and Japanese queries and invocation limits without using PostgreSQL or providers, run:

```powershell
dotnet run --project .\KeiBuild.Research -- plan
```

### Visual Studio

Open `KeiBuild.slnx` and set **KeiBuild.Research** as the startup project. In **Project Properties → Debug → Open debug launch profiles**, set the working directory to the solution folder and set command-line arguments to `demo ha4-demo`, `status ha4-demo`, or another command. Launch Visual Studio from an environment with the connection string set, or run the PowerShell commands from the integrated terminal. The `plan` command also works from F5 without configuration.

## Run a bounded live campaign

Set API keys in the process environment. OpenAI defaults to `gpt-4o-mini`. To select another supported Structured Outputs model, set `KEIBUILD_OPENAI_MODEL`. [Brave Web Search API](https://api-dashboard.search.brave.com/app/documentation/web-search/get-started) · [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)

```powershell
$env:BRAVE_SEARCH_API_KEY = ([System.Net.NetworkCredential]::new('', (Read-Host 'Brave Search API key' -AsSecureString))).Password
dotnet run --project .\KeiBuild.Research -- start ha4-live
dotnet run --project .\KeiBuild.Research -- discover ha4-live --search-limit 4 --budget-usd 0.05
dotnet run --project .\KeiBuild.Research -- sources ha4-live
```

Search stores ranked source leads. Review the exact URLs and their permissions before adding policies. Copy [the policy example](research-policies.example.json) to an ignored local path and replace the placeholder publishers and URLs. Record the basis for automated collection, AI processing and retention of the supporting excerpt. Set each permission to `true` only for the activity it covers.

```powershell
New-Item -ItemType Directory -Force .\.local\research | Out-Null
Copy-Item .\docs\research-policies.example.json .\.local\research\ha4-policies.json
```

- For one page, set `scope` to the exact registered public HTTPS URL and `prefix` to `false`.
- For a permitted section, set `prefix` to `true` and use a path ending in `/`. Query strings belong only in exact rules. An exact rule overrides a broader prefix. Subdomains require their own rules.
- `allowFetch`, `allowAi`, and `allowEvidence` are required for technical collection and analysis. `allowSnapshot` separately permits storage of the original bytes.
- Enable `exploreLinks` for same-origin links from approved HTML pages. An optional `sitemapUrl` authorizes that exact same-origin URL-set sitemap. Every resulting technical URL still needs a matching collection policy.
- New keys add rules and existing keys update them. To revoke fetch permission on an existing rule, import that key with `allowFetch: false`. Previously collected records retain their provenance.

Import the edited file and request a small batch.

```powershell
dotnet run --project .\KeiBuild.Research -- policies ha4-live --file .\.local\research\ha4-policies.json
$env:OPENAI_API_KEY = ([System.Net.NetworkCredential]::new('', (Read-Host 'OpenAI API key' -AsSecureString))).Password
dotnet run --project .\KeiBuild.Research -- work ha4-live --page-limit 2 --ai-limit 2 --budget-usd 0.10
dotnet run --project .\KeiBuild.Research -- claims ha4-live
```

Live collection reuses the Phase 2 collector with DNS checks, robots rules, no redirects, response limits, and per-origin pacing. It processes HTML, Markdown, and plain text. The source list shows approvals and holds so you can distinguish waiting jobs from completed work. [Robots Exclusion Protocol](https://www.rfc-editor.org/rfc/rfc9309.html)

## Resume and inspect progress

- `run` and `resume` handle discovery and due crawl jobs. Use `--search-limit 0` to resume crawl work only. Work saved before an interruption is reused. Calls already reserved may be counted even if the process stops before a result is saved.
- Transient failures are deferred with backoff. `status` shows recent errors, next due times, processed versions, claims, and usage. `retry-failed` explicitly requeues terminal failures after you correct the cause. Source-policy denials remain blocked.
- `--refresh-hours 168` makes completed documents older than seven days eligible for a fresh check. Each retrieved version is content-hashed. A matching hash and document format reuse the analysis and preserve URL-specific evidence.
- One database writer runs at a time. `--concurrency` bounds fetches across distinct live origins. Each live page attempt can make a robots request as well as the document request. The HTTP throttle files default to `.local/research/http`. Set `KEIBUILD_RESEARCH_STATE_DIR` to a consistent private location for multiple launch directories.
- `--search-limit`, `--page-limit`, `--ai-limit`, and `--max-bytes` are enforced per invocation. `--budget-usd` uses the operator-provided `--search-cost-usd` and `--ai-cost-usd` estimates. The defaults are planning values. OpenAI token totals are recorded when returned. Compare the estimates with your provider usage and pricing.

```powershell
dotnet run --project .\KeiBuild.Research -- status ha4-live
dotnet run --project .\KeiBuild.Research -- resume ha4-live --search-limit 0 --page-limit 2 --ai-limit 2
```

The database uses a `research` schema for campaigns, queues, attempts, claims and cited versions. The `catalog` schema stores vehicle identity, exact manufacturer and part-number identities, and approved-knowledge references. In `psql`, inspect `\dt research.*`, `\dt catalog.*`, and tables such as `research."Claims"` and `research."Evidence"`. The `claims` console command brings together the claim, source identity, document/version IDs, original passage, hash, retrieval time, and optional machine translation. Conflicting values in the same stated scope are flagged for review. Different manufacturers with the same number are possible matches, and the underlying records remain distinct.

## Review and export

Use the claim ID from `claims`. Review the effect of production dates, engine, drivetrain, transmission, trim, and exclusions before making a decision. Confirm permission for excerpt reuse in the intended destination before exporting.

```powershell
$keiClaimId = Read-Host 'Claim UUID'
dotnet run --project .\KeiBuild.Research -- review ha4-live --claim $keiClaimId --decision approve --note "Reviewed the source and exact HA4 application."
dotnet run --project .\KeiBuild.Research -- export ha4-live --output .\.local\research\ha4-approved.json
```

The export includes reviewed fitments that satisfy the existing JSON contract. Use the printed next offset with `--offset` for another batch. General technical facts stay in PostgreSQL. Synthetic campaigns can be reviewed as an exercise and are excluded from catalog promotion and export. Paste a non-empty live export into the existing Development **Review inbox** to use the current app approval flow. A future Blazor migration can query the approved catalog schema and join supporting evidence while retaining the existing public review boundary.

## Tests and next steps

```powershell
dotnet test .\KeiBuild.slnx
```

To include PostgreSQL integration tests, set `KEIBUILD_RESEARCH_TEST_DB` to a separate disposable database before running the tests. The tests apply migrations and create unique synthetic campaigns. They use fake providers and incur no paid API usage.

Before a larger campaign, check a handful of permitted real HA4 documents against hand-reviewed extractions, including Japanese tables, cross-references, and exclusions. Configure actual provider budgets and document the cadence and reuse terms for each publisher. The current proofing rules expect explicit labeled facts and exact identifiers, so ambiguous layouts may need additional normalization. Text-based PDF extraction, JavaScript rendering, sitemap indexes, and distributed crawl workers are future additions.

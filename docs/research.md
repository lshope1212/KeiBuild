# Kei truck research campaigns

**KeiBuild.Research** is a .NET 10 console-hosted application. It has its own PostgreSQL connection and provider credentials. Run it independently of the Blazor project.

## Start and monitor HA4 on Windows

Run these commands from the repository root with Docker Desktop running and Codex signed in with ChatGPT. The launcher uses the existing `ha4-live` campaign. It starts the Compose database if needed, applies pending migrations, finds the native executable behind `codex.cmd`, and tests the same restricted Windows profile and child environment that extraction uses.

```powershell
.\scripts\research.ps1 pilot HA4
.\scripts\research.ps1 status HA4
.\scripts\research.ps1 claims HA4
.\scripts\research.ps1 start HA4 -Hours 8
.\scripts\research.ps1 logs HA4 -Follow
.\scripts\research.ps1 stop HA4
.\scripts\research.ps1 resume HA4
```

`pilot` launches a detached session capped at 15 minutes, four searches, three page or metadata attempts and two extractions. Check `status` and `claims` for completed documents and evidence. A successful launch only reports that a worker started. The normal session defaults to eight hours, 16 searches, 12 page attempts and six extractions. Pass `-SearchLimit`, `-PageLimit`, or `-AiLimit` to change these caps. Start and resume remember these settings. Each invocation has its own budget and exits when permitted work, limits or time run out. The default Brave estimate is capped at $0.20 per invocation. Codex uses the ChatGPT account. Stop requests finish the active batch. Ctrl+C in the log viewer closes the viewer.

The ignored `.local/research/launcher/` directory contains timestamped progress logs, final status JSON, saved bounds, and a worker record with PID and creation time. The launcher checks both process identity and the database writer lease. If Windows PowerShell blocks locally authored scripts, use a process-scoped invocation such as `powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\research.ps1 status HA4`.

### Discovery and publisher review

By default, both discovery and extraction use the local Codex ChatGPT login. No API key or URL list is required. Each discovery invocation runs a restricted, non-interactive Codex turn with live web search. CLI 0.170.0 on Windows requires the bundled code-mode host and standalone web-search feature for this profile. Shell execution, browser controls, connectors and sandbox network access are disabled. Extraction runs separately with web search disabled. If the account or CLI cannot provide web-search events the session pauses. [Codex non-interactive execution](https://developers.openai.com/codex/noninteractive) · [Codex configuration](https://developers.openai.com/codex/config-basic)

Codex selects reference IDs using a strict schema. The engine resolves each ID against the web tool's results from that invocation. The tool supplies the URL, title and snippet. PostgreSQL retains the selected references per search attempt, including the normalized URL, publisher host, search-result or observed-page provenance, category, language and relevance reason. Only approved documents fetched by the collector can supply passages for claims. Codex campaigns start with a bilingual research plan and a Japanese open-license reference query. Other provider modes also retain the two legacy HA4 Wikipedia entry points.

Each campaign keeps English and Japanese seed queries, evidence-driven gaps and focused follow-ups in PostgreSQL. The discovery prompt includes category coverage, previous questions, observed parts and conflicts, and titles of links found in collected documents. The engine validates follow-ups against the target and plan, suppresses duplicate query text and shares the lifetime `--expansion-limit` (default 32) with evidence-driven gap searches. Use `coverage` for completed searches, grounded coverage and outstanding questions.

Select `-SearchProvider brave` in the launcher or `--search-provider brave` in the CLI to use Brave. Supply a subscription that permits result storage. To store its credential locally, run `.\scripts\research.ps1 configure HA4 -Setting Brave`. Windows-user DPAPI storage also supports the optional external database connection and existing Compose password. For keyless bibliographic discovery select `crossref`. Its vehicle-specific coverage varies. [Crossref REST API](https://www.crossref.org/documentation/retrieve-metadata/rest-api/) · [Brave Search API storage rights](https://brave.com/search/api/)

The worker checks public HTTPS, robots, content type, publisher restrictions and origin cooldowns. An existing scoped permission can cover additional URLs. For a new relevant publisher, the worker can inspect response headers under the same robots and pacing controls. Automatic approval requires a supported document type, an explicit `TDM-Reservation: 0` and an unambiguous HTTP license link to CC0. It checks the grant again on GET. The built-in Wikipedia rule checks the article's CC BY-SA 4.0 marker before processing article text, excludes embedded media and marked quotations, retains attribution and skips raw snapshots. Preserve attribution, license and applicable share-alike terms when reusing passages or translations. [TDM Reservation Protocol](https://www.w3.org/2022/tdmrep/) · [Wikimedia Terms of Use](https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use)

```powershell
.\scripts\research.ps1 sources HA4
.\scripts\research.ps1 review HA4
.\scripts\research.ps1 coverage HA4
```

For a publisher that has documented permission for automated retrieval, AI processing and evidence retention, approve a slash-terminated HTTPS path once. Supply the publisher's name and actual terms URL or written permission reference. A root path covers that host. The optional `-Format` accepts `html`, `text` or `markdown`. Source approvals are writer operations. If a worker holds the lease, stop it and wait for status to show it has exited, apply the approval, then resume. Access holds and unsupported formats remain visible in the review queue. Claim review is independent.

```powershell
.\scripts\research.ps1 approve HA4 -Scope $permittedPath -Publisher $publisherName -Basis $permissionReference
.\scripts\research.ps1 resume HA4
```

The CLI below is also available for direct administration, non-launcher campaigns and bulk policy imports.

## Set up PostgreSQL

From the solution folder in PowerShell, start the local PostgreSQL 17 service with Docker Compose. Enter a local development password at the prompt.

```powershell
$env:KEIBUILD_POSTGRES_PASSWORD = ([System.Net.NetworkCredential]::new('', (Read-Host 'PostgreSQL password' -AsSecureString))).Password
docker compose up -d --wait research-db
$env:KEIBUILD_RESEARCH_DB = 'Host=127.0.0.1;Port=5444;Database=keibuild;Username=keibuild;Password="{0}"' -f $env:KEIBUILD_POSTGRES_PASSWORD.Replace('"', '""')
```

For a PostgreSQL server you already run, create a dedicated development database and a login with permission to create schemas and tables in it. Set `KEIBUILD_RESEARCH_DB` to its Npgsql connection string and use your server’s TLS settings for remote connections. The Compose service binds to localhost. Its named volume persists data across restarts. [PostgreSQL installers](https://www.postgresql.org/download/) · [Npgsql connection strings](https://www.npgsql.org/doc/connection-string-parameters.html)

Apply the checked-in EF Core 10 migrations to a new or existing research database.

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

To preview the 28 base English and Japanese queries and invocation limits without using PostgreSQL or providers, run:

```powershell
dotnet run --project .\KeiBuild.Research -- plan
```

### Visual Studio

Open `KeiBuild.slnx` and set **KeiBuild.Research** as the startup project. In **Project Properties → Debug → Open debug launch profiles**, set the working directory to the solution folder and set command-line arguments to `demo ha4-demo`, `status ha4-demo`, or another command. Launch Visual Studio from an environment with the connection string set, or run the PowerShell commands from the integrated terminal. The `plan` command also works from F5 without configuration.

## Run a bounded live campaign

Install the official native Codex CLI and check its login under the same Windows account that runs the engine. Use a CLI supporting the options in `codex exec --help`, including strict configuration, ephemeral execution, and JSON Schema output. The launcher was verified with native CLI 0.170.0. The launcher and direct CLI can resolve the official Windows npm installation behind `codex.cmd`. For a custom native installation, set `KEIBUILD_CODEX_EXE` to the absolute path of the official native executable. [Codex CLI installation](https://developers.openai.com/codex/cli) · [Authentication](https://developers.openai.com/codex/auth)

```powershell
codex --version
codex login status
# If needed, run this once and select Sign in with ChatGPT.
codex login
codex login status
```

On native Windows the provider selects Microsoft Execution Containers (MXC). The legacy Windows sandboxes cannot enforce this workspace-only read profile. The following local check runs without model inference. It should print `MXC_OK`. Windows support depends on the OS build and managed policy. A host without MXC needs a supported environment for this provider, such as running the CLI and .NET worker together on Linux. [Windows sandbox compatibility and setup](https://developers.openai.com/codex/windows)

```powershell
codex -c windows.sandbox=mxc sandbox --include-managed-config --permission-profile :workspace -- cmd.exe /d /c echo MXC_OK
```

Look for `Logged in using ChatGPT`. Each extraction checks the login, restricts the auth method to ChatGPT and launches a fresh `codex exec` process. It receives numbered source lines and application-supplied provenance on stdin. Its working directory contains the schema, with a read-only workspace permission profile, disabled sandbox network, disabled shell and connector features, and no inherited database or inference keys. The CLI handles authentication and its own service connection. The application validates the final JSON, checks exact passages, and writes candidate claims. [Non-interactive execution](https://developers.openai.com/codex/noninteractive) · [Codex configuration](https://developers.openai.com/codex/config-reference)

To use the optional Brave provider, set its key and select it for discovery. [Brave Web Search API](https://api-dashboard.search.brave.com/app/documentation/web-search/get-started)

```powershell
$env:BRAVE_SEARCH_API_KEY = ([System.Net.NetworkCredential]::new('', (Read-Host 'Brave Search API key' -AsSecureString))).Password
dotnet run --project .\KeiBuild.Research -- start ha4-live
dotnet run --project .\KeiBuild.Research -- discover ha4-live --search-provider brave --search-limit 4 --budget-usd 0.05
dotnet run --project .\KeiBuild.Research -- sources ha4-live
```

Search stores ranked source leads and assesses eligibility. Use the launcher for a one-time scoped approval. For optional bulk imports, copy [the policy example](research-policies.example.json) to an ignored local path and replace the placeholder publishers and URLs. Record the basis for automated collection, AI processing and retention of the supporting excerpt. Set each permission to `true` only for the activity it covers.

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
dotnet run --project .\KeiBuild.Research -- work ha4-live --provider codex --continuous --max-minutes 30 --page-limit 6 --ai-limit 3 --budget-usd 0
dotnet run --project .\KeiBuild.Research -- claims ha4-live
```

For a two-hour Codex campaign, cap discovery turns, page attempts and extraction turns independently. A discovery turn can issue multiple web tool operations. Both kinds of Codex turn consume the account allowance and each has a five-minute timeout. The estimated API dollar budget applies to explicitly selected paid API providers. Status reports discovery and extraction token usage separately when the CLI reports it. Interrupted turns can leave a reservation without reported tokens.

```powershell
dotnet run --project .\KeiBuild.Research -- run ha4-live --search-provider codex --provider codex --continuous --max-minutes 120 --search-limit 20 --page-limit 40 --ai-limit 12 --budget-usd 0
```

For separately billed OpenAI API extraction, select the provider explicitly. `KEIBUILD_OPENAI_MODEL` selects an API model supporting structured outputs. The default is `gpt-4o-mini`. Status and summaries attribute calls and reported tokens to `Codex/ChatGPT`, `OpenAI/<model>`, or the offline fixture provider. Older runs show an unrecorded provider. Codex authentication or quota errors pause the session and keep the affected job pending. A provider pause prints status and exits with code 1 so an unattended caller can detect it. Consult Codex usage before resuming. The provider selection applies to each invocation. [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)

```powershell
$env:OPENAI_API_KEY = ([System.Net.NetworkCredential]::new('', (Read-Host 'OpenAI API key' -AsSecureString))).Password
dotnet run --project .\KeiBuild.Research -- work ha4-live --provider openai --page-limit 2 --ai-limit 2 --budget-usd 0.10
```

Live collection reuses the Phase 2 collector with DNS checks, robots rules, no redirects, response limits, and per-origin pacing. It processes HTML, Markdown, and plain text. The source list shows approvals and holds so you can distinguish waiting jobs from completed work. [Robots Exclusion Protocol](https://www.rfc-editor.org/rfc/rfc9309.html)

## Resume and inspect progress

- In continuous mode the worker alternates batches of up to four searches and four page attempts. The request counts and estimated dollar ceiling span the entire invocation. It waits for deferred eligible work until the deadline, and exits when the queue is exhausted, policy-blocked, or beyond the remaining limits. A new invocation receives a fresh budget. Continuous mode accepts up to 10,000 attempts of each type and a maximum of 10,080 minutes.
- From a second PowerShell window with the same connection string, use `stop` to finish the active work and release the lease. Ctrl+C cancels in-flight operations, including the Codex subprocess. Recovery requeues interrupted attempts and preserves completed versions, claims and reviews. When snapshot retention was authorized and remains allowed, a retry can extract a saved, unprocessed version. Otherwise it fetches the page again to reconstruct the input. A crash between an external request and its durable result can repeat that request. Status includes reservations made before interruption. Resume clears a previous stop request.
- Use `jobs` for pending, running, failed, and blocked query/crawl details. Use `sources` and `claims` for collected URLs and evidence. These commands page 100 items at a time with `--offset`. Read-only diagnostics and `stop` work while the writer lease is held.
- `coverage` shows completed searches, counts of evidence-backed candidate or approved claims by topic, and outstanding follow-up questions. Follow-ups use grounded claims about unresolved part identities, conflicting values, and single-source technical findings. These evidence-driven queries preserve an originating claim ID and priority. Codex-planned queries retain a category, reason, language and queue state. Corroboration counts distinct document content so a mirror cannot close a gap. Japanese references count claims supported by Japanese-language versions. The campaign-wide `--expansion-limit` defaults to 32. Zero disables new follow-ups. A successful search alone supplies no evidence.
- `summary` writes a new JSON report of operational state, usage, coverage and gaps. Use `export` for the separate approved-fitment contract.

```powershell
dotnet run --project .\KeiBuild.Research -- status ha4-live
dotnet run --project .\KeiBuild.Research -- jobs ha4-live
dotnet run --project .\KeiBuild.Research -- coverage ha4-live
dotnet run --project .\KeiBuild.Research -- stop ha4-live
dotnet run --project .\KeiBuild.Research -- resume ha4-live --provider codex --continuous --max-minutes 30 --search-limit 0 --page-limit 6 --ai-limit 3 --budget-usd 0
dotnet run --project .\KeiBuild.Research -- summary ha4-live --output .\.local\research\ha4-summary.json
```

- Transient failures are deferred with backoff. `status` shows recent errors, next due times, processed versions, claims, and usage. `retry-failed` explicitly requeues terminal failures after you correct the cause. Source-policy denials remain blocked.
- `--refresh-hours 168` makes completed documents older than seven days eligible for a fresh check. Each retrieved version is content-hashed. A matching hash and document format reuse the analysis and preserve URL-specific evidence.
- One database writer runs at a time. `--concurrency` bounds fetches across distinct live origins. Each live page attempt can make a robots request followed by a document GET or metadata HEAD. Crossref requests also honor its robots file and origin pacing. The HTTP throttle files default to `.local/research/http`. Set `KEIBUILD_RESEARCH_STATE_DIR` to a consistent private location for multiple launch directories.
- `--search-limit`, `--page-limit`, `--ai-limit`, and `--max-bytes` are enforced per invocation. `--budget-usd` uses the operator-provided `--search-cost-usd` and `--ai-cost-usd` estimates. The defaults are planning values. OpenAI token totals are recorded when returned. Compare the estimates with your provider usage and pricing.

The database uses a `research` schema for campaigns, queues, attempts, claims and cited versions. The `catalog` schema stores vehicle identity, exact manufacturer and part-number identities, and approved-knowledge references. In `psql`, inspect `\dt research.*`, `\dt catalog.*`, and tables such as `research."Claims"` and `research."Evidence"`. The `claims` console command brings together the claim, source identity, document/version IDs, original passage, hash, retrieval time, and optional machine translation. Conflicting values in the same stated scope are flagged for review. Different manufacturers with the same number are possible matches, and the underlying records remain distinct.

## Add another target

Supply a JSON target to `start --target-file`. Each campaign retains its database vehicle variant and seed queries. Resume by campaign name. Search relevance, extraction schema, validation and export use the stored target chassis. The offline fixtures cover HA4. Use a new campaign name for a different vehicle or engine. For example, save the following as `.local/research/ha3.json` and adjust the reference period and names for your research scope.

```json
{
  "make": "Honda", "model": "Acty Truck", "chassis": "HA3", "engine": "E07A",
  "era": "1990s reference period", "englishName": "Honda Acty", "japaneseName": "ホンダ アクティ"
}
```

```powershell
dotnet run --project .\KeiBuild.Research -- plan ha3-live --target-file .\.local\research\ha3.json
dotnet run --project .\KeiBuild.Research -- start ha3-live --target-file .\.local\research\ha3.json
```

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

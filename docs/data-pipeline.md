# Single-document ingestion

The pipeline produces candidates for the existing Development review inbox. Start with a permitted manufacturer or vendor document for the Honda Acty HA4. Keep the exact build window, engine, transmission, and exclusions in the source excerpt.

## Run the offline fixture

From the solution directory in PowerShell, run the console with the included HTML document and fake extractor response.

```powershell
dotnet run --project .\KeiBuild.Ingestion -- --registry .\docs\sources.example.json --source synthetic-ha4 --file .\docs\fixtures\synthetic-fitment.html --fake-response .\docs\fixtures\synthetic-response.json
```

The program prints the full path to `candidates.json` under `.local/ingestion/runs/`. The fixture should produce one candidate with part number `00-FIXTURE-12`. Its product, publisher, URL, and fitment are fabricated. Use an isolated review file for the exercise.

The first build restores NuGet packages. Once the solution is built, add `--no-build` before `--` to run the offline example from the existing build.

In another PowerShell terminal, from the same solution folder, start the web app with a fresh review file.

```powershell
$env:KeiBuild__ReviewFile = Join-Path (Get-Location).Path ".local\ingestion\fixture-review-$([guid]::NewGuid()).json"
dotnet run --project .\KeiBuild --launch-profile http
```

1. Open [Review inbox](http://localhost:5117/review). Paste the contents of the printed `candidates.json` and select **Queue for review**.
2. Search [Parts & mods](http://localhost:5117/parts?q=00-FIXTURE-12) for the fixture part number. The result should appear only after you return to the inbox and explicitly approve the candidate.
3. Stop the test app when done. If you reuse that terminal for everyday web development, run `Remove-Item Env:KeiBuild__ReviewFile` to return to the default review file.

### Run in Visual Studio

1. Open `KeiBuild.slnx` and set **KeiBuild.Ingestion** as the startup project.
2. Open its debug launch profiles from **Project Properties → Debug**. Set **Working directory** to the solution folder.
3. Enter the following in **Command line arguments**, then press **F5**.

```text
--registry docs/sources.example.json --source synthetic-ha4 --file docs/fixtures/synthetic-fitment.html --fake-response docs/fixtures/synthetic-response.json
```

For live extraction use a PowerShell session with the provider environment configured. Visual Studio’s integrated PowerShell terminal can run the commands below.

## Run a live extraction from a local file

Create a private registry from the example and replace the synthetic record with verified metadata for your permitted source.

```powershell
New-Item -ItemType Directory -Force .\.local | Out-Null
Copy-Item .\docs\sources.example.json .\.local\sources.json
```

Use `ha4-product-page` as the record’s `id` for the commands below. The record requires a publisher, source title, original HTTP(S) URL, classification (`Manufacturer`, `Vendor`, or `Community`), matching format (`Html`, `Markdown`, or `Text`), and permission or reuse notes. Set `automatedRetrievalApproved` to `false` for local-only use. Set `snapshotAllowed` according to your permission to store a copy. Use `language` and an ISO 8601 `lastRetrievedAtUtc` when known. The example shows all fields.

Save the permitted document in UTF-8 under `.local`, with the corresponding extension. Using `--file` reads the local document. OpenAI still receives the normalized text in live extraction mode, so use material you can send to the configured provider.

```powershell
$env:OPENAI_API_KEY = ([System.Net.NetworkCredential]::new('', (Read-Host 'OpenAI API key' -AsSecureString))).Password
dotnet run --project .\KeiBuild.Ingestion -- --registry .\.local\sources.json --source ha4-product-page --file .\.local\ha4-product-page.html
```

The console uses the official OpenAI .NET SDK behind `IClaimExtractor`. Live mode defaults to `gpt-4o-mini`. Set `KEIBUILD_OPENAI_MODEL` or pass `--model` to choose a model that supports strict Structured Outputs and this schema. The command-line model takes precedence. The API key lives in the current terminal’s environment. [OpenAI .NET SDK](https://github.com/openai/openai-dotnet#how-to-use-chat-completions-with-structured-outputs)

## Retrieve an explicitly approved URL

Review the source’s automated access terms and document your approval basis in the registry before setting `automatedRetrievalApproved` to `true`. Register the exact final product-page URL and its document format. The fixture record is intentionally local-only.

```powershell
dotnet run --project .\KeiBuild.Ingestion -- --registry .\.local\sources.json --source ha4-product-page --url
```

The collector checks `robots.txt` and requests the exact registered public HTTPS URL. It uses a public IPv4 connection, skips redirects, validates response types, limits each response to 512 KiB and 20 seconds, and spaces requests by origin. It honors supported robots crawl delays up to 30 seconds and stops at access refusals or redirects. In those cases you can supply a permitted local copy. The current workflow accepts HTML, Markdown, and plain text. PDF extraction and JavaScript rendering are future collection options. [Robots Exclusion Protocol](https://www.rfc-editor.org/rfc/rfc9309.html)

## What the pipeline exports

- `candidates.json` is the existing [review import array](fitment-import.schema.json). A nonempty result fits the inbox’s limits of 50 entries and 40,000 characters. An empty array means there is nothing to submit. Use `--output .\.local\ingestion\my-run` to choose a new output directory.
- `provenance.json` records the source and permissions, publisher, language, collection and retrieval times when known, SHA-256 of the original bytes, normalizer version, extractor, and withheld-row reasons. An HTTP run records its retrieval timestamp here. The source registry is maintained separately, so it can remain a record of source permissions.
- With `--save-snapshot` and `snapshotAllowed: true`, the original bytes are saved as `source.html`, `source.md`, or `source.txt`. Keep registries and run outputs under the Git-ignored `.local` directory.

The [extraction prompt](../KeiBuild.Ingestion/Extraction/ExtractionPrompt.cs) asks the model for typed drafts with evidence line ranges. The application attaches the registry URL, type, and title, copies the normalized excerpt, and supplies the line locator. It checks for a known chassis, an explicit application line, exact part identifiers, and a labeled OEM reference when present. The shared catalog validator then checks the import contract and applies the same duplicate comparison as the inbox. Distinct recorded conditions stay separate so the reviewer can compare them.

Evidence rules deliberately favor explicit applications. Some tables with merged cells, indirect compatibility, lengthy qualifiers, or abbreviated chassis codes may need a clearer permitted excerpt or manual research. Automated checks verify structure and textual support. Review the actual source, scope, and conflicting applications before approving real records.

## Run tests

```powershell
dotnet test .\KeiBuild.slnx
```

The tests use a file-backed extractor or in-memory doubles. The fixture test exports the candidates, imports them through the existing catalog store, and verifies they become visible only after manual approval and remain visible after restart. Visual Studio’s **Test Explorer** can run the same tests.

## Before expanding collection

Validate one permitted real HA4 document with live extraction. Compare the resulting candidates with a hand-checked set, including at least one variant split or exclusion, and keep that source as a regression fixture if permission permits. Use the comparison to refine how the console handles ambiguity. Then expand the source registry and add incremental collection and durable run history when the number of approved sources calls for them.

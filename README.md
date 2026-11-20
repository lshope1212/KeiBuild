# KeiBuild

KeiBuild is a Blazor Web App for looking up Japanese kei trucks by chassis and finding parts and modifications with traceable fitment evidence. The initial catalog covers Honda Acty HA3/HA4 and Suzuki Carry DD51T/DA16T. The Honda Acty HA4 is the first reference vehicle for growing the catalog.

## Run locally

Open `KeiBuild.slnx` in Visual Studio with the .NET 10 SDK and ASP.NET and web development workload. Select the **http** launch profile and press **F5**.

You can also start it from the solution folder.

```powershell
dotnet run --project KeiBuild --launch-profile http
```

Open [KeiBuild on localhost](http://localhost:5117/).

1. Search for **HA4** in the chassis index and open the **Honda Acty Truck**. Expand **Source details** on the chassis and on a listing.
2. Open **Parts & mods** and search for `19045-PR3-004`. The chassis filter narrows that OEM cross-reference to one truck.
3. Open **Review inbox** and paste the entire [example import](docs/example-import.json). Check the source and approve the candidate. Search for `P561A` on **Parts & mods** to see the new DD51T listing. Reimport the same batch to see the duplicate check.

The review inbox runs in the **Development** environment and the supplied launch profiles bind to localhost. The inbox and its decisions persist in `KeiBuild/.local/review.json`. A production run serves the checked-in catalog. To promote a locally approved listing to the checked-in catalog, add the item’s `entry` from the review file to the `entries` array in `KeiBuild/Data/catalog.json`, then restart. A local run displays that promoted claim once even if its review history is still present.

## Try the ingestion pipeline

Run this from the solution folder in PowerShell. It uses a clearly labeled synthetic document and a fake extractor, so it works without an API key.

```powershell
dotnet run --project .\KeiBuild.Ingestion -- --registry .\docs\sources.example.json --source synthetic-ha4 --file .\docs\fixtures\synthetic-fitment.html --fake-response .\docs\fixtures\synthetic-response.json
```

The console prints the path to a review-ready `candidates.json`. Use the [data pipeline guide](docs/data-pipeline.md) to import the fixture into an isolated inbox, configure OpenAI for a real document, run the console in Visual Studio, or retrieve an approved URL.

`dotnet test .\KeiBuild.slnx` runs the xUnit tests.

## Find your way around

| File | Role |
| --- | --- |
| `KeiBuild/Components/Pages/` | Razor pages for the chassis index, chassis details, parts search, and review inbox. Razor components are the closest equivalent to a WPF view or user control. |
| `KeiBuild/Catalog/` | Chassis and review models. The store loads the catalog and saves review decisions. |
| `KeiBuild.Catalog/` | Fitment and source records, import validation, JSON serialization, and duplicate comparison shared by the web and console apps. |
| `KeiBuild.Ingestion/` | Console app for single-document collection, normalization, extraction, validation, and export. |
| `KeiBuild.Ingestion.Tests/` | xUnit coverage for collection policy, grounding, validation, and the offline review workflow. |
| `KeiBuild.Research/` | Independent PostgreSQL research console for HA4 campaigns, discovery, sourced technical claims, and reviewed exports. |
| `KeiBuild.Research.Tests/` | Research unit tests and opt-in PostgreSQL integration tests using synthetic providers. |
| `KeiBuild/Data/catalog.json` | Starter chassis and fitment claims with source locators. |

The app uses .NET 10 and static server rendering. Search is an ordinary GET request with a shareable URL, and the review page submits standard POST forms. Blazor’s per-page Interactive Server mode can be added when a feature needs live state, such as comparing options in a build planner. [Microsoft’s Blazor render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0)

From PowerShell, run `.\scripts\research.ps1 pilot HA4` to discover sources and extract evidence using your local Codex ChatGPT login. No Brave key or manual URL list is required. Inspect `.\scripts\research.ps1 status HA4` for provider usage, `coverage HA4` for open questions, and `review HA4` for sources awaiting permission. Use `stop HA4` and `resume HA4` to control the worker. The [research guide](docs/research.md) covers Windows setup, optional Brave discovery, publisher approval, campaign limits and exports.

<details>
<summary>Hero image provenance</summary>

The built-in image generator created the [garage illustration](KeiBuild/wwwroot/images/kei-truck-garage.jpg).

> Use case: photorealistic natural editorial imagery. Asset: wide hero photograph for KeiBuild, a chassis catalog for Japanese kei trucks. A single well-kept ivory white late-1990s Japanese cab-over kei mini pickup with a tiny two-door cab, narrow road tires, and a clearly visible short flat drop-side cargo bed. Parked three-quarter front view, front facing left, on dark concrete in a modest vintage Japanese service garage with muted forest green walls, industrial architectural shadows, and soft window light on the cab. Prominent truck filling the center and right of the wide image, all four extremities visible with breathing room, quiet empty dark left quarter, camera at headlight height, realistic geometry, warm ivory, moss and charcoal palette, fine editorial film grain. Wide landscape photograph. No lettering, logos, people, readable plates, watermarks, diagrams or overlays.

</details>

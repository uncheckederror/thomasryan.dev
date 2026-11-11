# Repository guidance

This is a static GitHub Pages site. Preserve existing uncommitted changes and keep
main-site HTML pages standalone, with CSS and JavaScript inline. The pages under
`demos/` are separate standalone demos; do not treat them as main-site templates.
Use standalone C# scripts for maintenance rather than adding Python tooling.

## Common tasks

- **Posts:** Edit the article in its year/month directory and keep its publication
  date and `BlogPosting` JSON-LD consistent with `posts.html`. For a new post, also
  update the latest-post section in `index.html` and `sitemap.txt`.
- **Feed:** After archive changes or an article's `dateModified` change, run
  `dotnet run generate_feed.cs`. Preserve the existing `feed.xml`: it stores
  update timestamps that cannot be reconstructed from the archive. Date-only
  values assume midnight in Seattle. See `README.md` for modification-date rules.
- **Shared styles:** Edit the marked shared blocks in `index.html`, then run
  `dotnet run sync_inline_assets.cs`. Page-specific styles remain in their pages.
  Run the same command after adding or removing a code block or sitemap page.
- **CSP:** After changing inline JavaScript, adding an HTML page, or running the
  inline sync command, run `dotnet run sync_csp.cs`; check with
  `dotnet run sync_csp.cs -- --check`. Main-site policies allow Clarity and
  hash inline scripts; standalone demos have a more permissive policy.
- **Local preview:** Open an HTML file directly, or run `dotnet run serve_site.cs`
  and use http://localhost:8000/ for root-relative links.

## Checks

Scripts require .NET SDK 10.0.300 or later; the feed, CSP, and site-check scripts
restore AngleSharp from NuGet on first use. Neither .NET nor NuGet is needed to view HTML.
From the repository root:

- `dotnet run tests/site_checks.cs` runs all checks, as in CI.
- `dotnet run tests/site_checks.cs -- --feed --inline` runs selected groups.
  Other groups are `--site` and `--browser`; `--site` also verifies the current
  feed and shared inline assets.
- `dotnet run generate_feed.cs -- --check` and
  `dotnet run sync_inline_assets.cs -- --check` check generated files without writing.

Browser checks use Chrome, Chromium, or Edge; set `CHROME_BIN` if needed. They
skip locally if no browser is found, but fail in CI. Tests block external services.
CI also audits direct and transitive NuGet dependencies weekly.

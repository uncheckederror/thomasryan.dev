# thomasryan.dev

The source for [thomasryan.dev](https://thomasryan.dev/), Thomas August Ryan's personal website, portfolio, and blog about GIS, software development, and other interests.

The site is built with static HTML and CSS and hosted on GitHub Pages with a custom domain. The [demos](demos/) are standalone pages, separate from the main site and its similarly styled pages; some use JavaScript and external services.

## Local preview

Clone the repository and open [index.html](index.html), or another HTML file, in a browser. No SDK or build step is required.

Alternatively, to preview the site over HTTP, run this from the repository root with the .NET SDK:

```shell
dotnet run serve_site.cs
```

Open [http://localhost:8000/](http://localhost:8000/). A local server makes root links behave as they do on the hosted site. Some demos require external services, and some pages load Microsoft Clarity analytics, including during local previews.

## Content

- [index.html](index.html) is the home page.
- [work.html](work.html) and [posts.html](posts.html) list projects and posts.
- Posts are stored in year and month directories; [demos](demos/) contains standalone interactive examples.
- When publishing a post, add its publication date and `BlogPosting` JSON-LD to the article, then update [posts.html](posts.html), the latest-post section of [index.html](index.html), and [sitemap.txt](sitemap.txt). The sitemap is referenced by [robots.txt](robots.txt).
- The archive search filters locally in the browser; without JavaScript all entries remain visible. Each local article has static newer/older links in archive order. When publishing or reordering local articles, update those links on the affected articles; external posts and PDFs are excluded.
- For large article photos, use an optimized display image (up to 1600 pixels wide) and link it to the full-size original. Supply the display image's dimensions to reserve space, keep responsive image heights automatic, and lazy-load photos below the initial viewport.
- The standalone C# maintenance scripts require the .NET SDK 10.0.300 or later. The feed generator, CSP synchronizer, and site checks restore the AngleSharp HTML parser from NuGet on first use; neither .NET nor the package is needed to view the HTML files directly.
- After updating the archive or an article's modification date, regenerate [feed.xml](feed.xml) with `dotnet run generate_feed.cs` and commit it with the content changes. The feed includes locally hosted HTML posts listed in the archive, not external posts or PDFs. Publication dates use midnight in Seattle (`America/Los_Angeles`) as an assumed time, with the appropriate daylight-saving offset.
- Feed entry updates advance automatically when an archive title or summary changes. For substantive article changes, add or update `dateModified` in the article's `BlogPosting` JSON-LD and regenerate the feed. Use a `YYYY-MM-DD` date (assumed midnight in Seattle), or a timestamp with an explicit UTC offset. Preserve the existing feed when regenerating: it records update times for archive edits, which cannot be reconstructed from the archive alone.
- The feed generator writes a temporary file beside the feed before replacing it. The inline asset synchronizer also uses temporary files and checks for intervening edits. Both commands require permission to create and replace files in the relevant directories.
- Run `dotnet run generate_feed.cs -- --check` to verify that the feed is current. [llms.txt](llms.txt) lists entry points for agents.

## Maintaining and checking the site

The main-site pages keep their CSS and JavaScript inline, so they can be opened directly without a build step. The marked shared style blocks in [index.html](index.html) are the source for common styles; page-specific styles remain in each page. After changing a shared block or adding a page to the sitemap, run `dotnet run sync_inline_assets.cs` to copy those styles and the code-scrolling behavior into the applicable pages. Use `dotnet run sync_inline_assets.cs -- --check` to check for stale copies.

Keep code examples inline in the article. Existing examples link to their original Gists, but are local snapshots and must be updated in the article when the Gist changes. Give each `<pre>` a `tabindex="0"`, `role="region"`, and a descriptive `aria-label` so its content can be scrolled with the keyboard. Run the sync command after adding or removing a code block.

Run the checks with the .NET SDK:

```shell
dotnet run tests/site_checks.cs
```

The checks cover local links, article navigation, inline assets, the sitemap and feed, archive search and its no-JavaScript fallback, responsive navigation and overflow, code scrolling, and ResuMap chapter transitions with a mocked map. The browser check uses an installed Chrome, Chromium, or Edge and is skipped locally if none is found. Set `CHROME_BIN` to the browser executable if it is installed somewhere else. The checks run locally without loading external services, and also run on pushes and pull requests in GitHub Actions.

To run specific groups, pass one or more options:

```shell
dotnet run tests/site_checks.cs -- --feed --inline
```

The groups are `--feed` (feed generator), `--inline` (inline asset synchronization),
`--site` (published pages, including current feed and inline assets), and `--browser`
(responsive layout, navigation, contrast, and code scrolling). Without options,
all groups run. Browser checks fail rather than skip if `CI` is set and no browser
is available. See [AGENTS.md](AGENTS.md) for a concise task map and repository guidance.

## Security maintenance

The HTML pages use a meta Content Security Policy (CSP), because GitHub Pages does
not provide configurable response headers. Main-site policies permit the existing
inline scripts by hash and allow Microsoft Clarity's documented domains. After
editing an inline script or adding an HTML page, run `dotnet run sync_csp.cs` and
commit the updated policy; `dotnet run sync_csp.cs -- --check` detects stale hashes.
Run it after `sync_inline_assets.cs` when changing shared code scrolling.

Every main-site page has an inline `speculationrules` script that prefetches
same-site links on hover, and a shared `@view-transition` rule (disabled for
reduced motion) for smoother navigation. The CSP allows the former with
`'inline-speculation-rules'` instead of a hash.

The older standalone demos use a more permissive CSP, including HTTPS script
sources and dynamic code evaluation, for their map and social integrations. A
meta CSP cannot enforce header-only directives such as `frame-ancestors`, and
these policies do not replace server-side security headers.

The site-check workflow runs on pushes, pull requests, and weekly. It performs a
fresh NuGet audit of direct and transitive dependencies and fails on advisories or
unavailable audit data. Review failed workflow notifications in GitHub; keep Actions
and notification settings enabled for the scheduled check to run and alert you.

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).

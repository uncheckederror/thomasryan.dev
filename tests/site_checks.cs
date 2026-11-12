#!/usr/bin/env dotnet
#:include ../generate_feed.cs
#:include ../sync_inline_assets.cs
#:include ../sync_csp.cs
#:property StartupObject=SiteChecks
#:property PublishAot=false
using AngleSharp.Html.Parser;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class SiteChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T19:30:00Z");
    private const string Entry = """
        <a href="./2024/01/post.html"><time datetime="2024-01-15">Jan 15</time><h3>Maps &amp; code</h3><p>A useful summary.</p></a>
        """;

    private static string SourceDirectory([CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;

    private static HashSet<string> SelectChecks(string[] args)
    {
        string[] names = ["feed", "inline", "site", "browser"];
        if (args.Length == 0) return names.ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || !names.Contains(arg[2..], StringComparer.Ordinal))
                throw new ArgumentException($"Unknown check: {arg}");
            selected.Add(arg[2..]);
        }
        return selected;
    }

    public static async Task<int> Main(string[] args)
    {
        const string usage = "Usage: dotnet run tests/site_checks.cs -- [--feed] [--inline] [--site] [--browser]";
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine(usage);
            Console.WriteLine("Run all checks by default, or select one or more groups.");
            return 0;
        }
        HashSet<string> selected;
        try { selected = SelectChecks(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"{ex.Message}\n{usage}");
            return 2;
        }
        try
        {
            if (selected.Contains("feed"))
            {
                TestFeed();
                Console.WriteLine("Feed generator checks passed.");
            }
            if (selected.Contains("inline"))
            {
                TestSync();
                TestCsp();
                Console.WriteLine("Inline asset checks passed.");
            }
            string root = Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));
            if (selected.Contains("site"))
            {
                TestSelection();
                TestSite(root);
                Console.WriteLine("Published site checks passed.");
            }
            if (selected.Contains("browser")) await TestResponsive(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Site checks failed: {ex}");
            return 1;
        }
    }

    private static void TestSelection()
    {
        Assert(SelectChecks([]).SetEquals(["feed", "inline", "site", "browser"]), "Default checks are incomplete");
        Assert(SelectChecks(["--feed", "--inline"]).SetEquals(["feed", "inline"]), "Check selection is incorrect");
        try { SelectChecks(["--feed", "--invalid"]); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Unknown check was accepted");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or
                                   System.Text.Json.JsonException or System.Xml.XmlException)
        {
            if (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException($"Expected error containing '{message}', got: {ex.Message}", ex);
        }
        throw new InvalidOperationException($"Expected error containing '{message}'");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("site-checks-").FullName;
        public string Post => Path.Combine(Root, "2024", "01", "post.html");
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Post)!);
            Metadata("2024-01-15");
        }
        public void Archive(string content) => File.WriteAllText(Path.Combine(Root, "posts.html"), "<main>" + content + "</main>");
        public void Metadata(string date, object? modified = null, bool hasModified = false)
        {
            var data = new Dictionary<string, object?> { ["@type"] = "BlogPosting", ["datePublished"] = date };
            if (hasModified) data["dateModified"] = modified;
            File.WriteAllText(Post, "<script type=\"application/ld+json\">" + JsonSerializer.Serialize(data) + "</script>");
        }
        public byte[] Save(DateTimeOffset? now = null)
        {
            byte[] content = GenerateFeed.Generate(Root, now ?? Now);
            GenerateFeed.WriteFeed(Root, content);
            return content;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static XElement Feed(byte[] content) => XDocument.Parse(Encoding.UTF8.GetString(content)).Root!;
    private static string? Field(XElement element, string name) => (string?)element.Element(GenerateFeed.Atom + name);
    private static XElement FirstEntry(XElement feed) => feed.Elements(GenerateFeed.Atom + "entry").First();

    private static void TestFeed()
    {
        using var f = new Fixture();
        f.Archive(Entry + "<a href=\"https://example.com/post.html\">External</a><a href=\"./2024/01/post.pdf\">PDF</a>");
        var feed = Feed(f.Save());
        Assert(feed.Elements(GenerateFeed.Atom + "entry").Count() == 1, "Feed included external or PDF links");
        Assert(Field(FirstEntry(feed), "title") == "Maps & code", "Feed did not decode HTML entities");
        Assert(Field(FirstEntry(feed), "published") == "2024-01-15T00:00:00-08:00", "Incorrect winter time");

        File.Delete(Path.Combine(f.Root, "feed.xml"));
        File.Move(f.Post, Path.Combine(Path.GetDirectoryName(f.Post)!, "post's name.html"));
        foreach (string href in new[] { "./2024/01/post%27s%20name.html", "/2024/01/post%27s%20name.html",
                                        "2024/01/post%27s%20name.html", "https://thomasryan.dev/2024/01/post%27s%20name.html" })
        {
            f.Archive(Entry.Replace("./2024/01/post.html", href));
            Assert(Field(FirstEntry(Feed(GenerateFeed.Generate(f.Root, Now))), "id") ==
                "https://thomasryan.dev/2024/01/post%27s%20name.html", $"Incorrect URL for {href}");
        }
        File.Move(Path.Combine(Path.GetDirectoryName(f.Post)!, "post's name.html"), f.Post);
        f.Archive(Entry + Entry.Replace("./2024/01/post.html", "/2024/01/post.html"));
        Throws(() => GenerateFeed.Generate(f.Root, Now), "Duplicate post URL");
        foreach (string href in new[] { "./2024/01/%2e%2e%2fpost.html", "./2024/01/..%5cpost.html" })
        {
            f.Archive(Entry.Replace("./2024/01/post.html", href));
            Throws(() => GenerateFeed.Generate(f.Root, Now), "Unexpected local post URL");
        }

        foreach (var (day, offset) in new[] { ("2024-01-15", "-08:00"), ("2024-03-10", "-08:00"),
                 ("2024-03-11", "-07:00"), ("2024-07-15", "-07:00"),
                 ("2024-11-03", "-07:00"), ("2024-11-04", "-08:00") })
        {
            f.Metadata(day);
            f.Archive(Entry.Replace("2024-01-15", day));
            Assert(Field(FirstEntry(Feed(GenerateFeed.Generate(f.Root, Now))), "published") ==
                day + "T00:00:00" + offset, $"Incorrect Seattle offset for {day}");
        }

        f.Metadata("2024-01-15");
        f.Archive(Entry);
        byte[] original = f.Save();
        var later = Now.AddDays(1);
        Assert(original.SequenceEqual(GenerateFeed.Generate(f.Root, later)), "Unchanged feed was not stable");
        f.Archive(Entry.Replace("A useful summary.", "A revised summary."));
        Assert(!original.SequenceEqual(GenerateFeed.Generate(f.Root, later)), "Changed archive did not make feed stale");
        byte[] revised = f.Save(later);
        feed = Feed(revised);
        Assert(Field(FirstEntry(feed), "updated") == "2026-08-02T12:30:00-07:00", "Summary update not recorded");
        Assert(Field(feed, "updated") == "2026-08-02T12:30:00-07:00", "Feed update not recorded");
        Assert(revised.SequenceEqual(GenerateFeed.Generate(f.Root, Now)), "Update timestamp did not remain stable");
        f.Archive(Entry.Replace("Maps &amp; code", "New title").Replace("A useful summary.", "A revised summary."));
        feed = Feed(f.Save(Now.AddDays(2)));
        Assert(Field(FirstEntry(feed), "updated") == "2026-08-03T12:30:00-07:00", "Title update not recorded");

        File.Delete(Path.Combine(f.Root, "feed.xml"));
        f.Archive(Entry);
        f.Save();
        f.Metadata("2024-01-15", "2024-07-15", true);
        Assert(Field(FirstEntry(Feed(f.Save())), "updated") == "2024-07-15T00:00:00-07:00", "Modification date ignored");
        f.Metadata("2024-01-15", "2024-07-16T18:15:00Z", true);
        Assert(Field(FirstEntry(Feed(f.Save())), "updated") == "2024-07-16T11:15:00-07:00", "Modification timestamp not converted");
        foreach (object? invalid in new object?[] { "2024-01-14", "2024-07-16T18:15:00", "not-a-date", 123, null })
        {
            f.Metadata("2024-01-15", invalid, true);
            Throws(() => GenerateFeed.Generate(f.Root, Now), "modification date");
        }

        f.Metadata("2024-01-15");
        File.Delete(Path.Combine(f.Root, "feed.xml"));
        string legacy = Encoding.UTF8.GetString(GenerateFeed.Generate(f.Root, Now))
            .Replace("2024-01-15T00:00:00-08:00", "2024-01-15T00:00:00Z");
        File.WriteAllText(Path.Combine(f.Root, "feed.xml"), legacy);
        byte[] migrated = f.Save();
        Assert(Field(FirstEntry(Feed(migrated)), "updated") == "2024-01-15T00:00:00-08:00", "Legacy UTC timestamp not migrated");
        Assert(migrated.SequenceEqual(GenerateFeed.Generate(f.Root, Now)), "Migrated feed was not stable");
        File.Copy(f.Post, Path.Combine(Path.GetDirectoryName(f.Post)!, "another.html"));
        f.Archive(Entry + Entry.Replace("post.html", "another.html"));
        feed = Feed(f.Save(later));
        Assert(Field(feed, "updated") == "2026-08-02T12:30:00-07:00", "Adding a post did not advance feed update");
        Assert(Field(feed.Elements(GenerateFeed.Atom + "entry").Last(), "updated") == "2026-08-02T12:30:00-07:00",
            "Adding a post did not record entry update");
        f.Archive(Entry);
        byte[] removed = f.Save(Now.AddDays(2));
        Assert(Field(Feed(removed), "updated") == "2026-08-03T12:30:00-07:00", "Removing a post did not advance feed update");
        Assert(removed.SequenceEqual(GenerateFeed.Generate(f.Root, Now)), "Removal timestamp did not remain stable");
        File.WriteAllText(Path.Combine(f.Root, "feed.xml"), "<feed>");
        Throws(() => GenerateFeed.Generate(f.Root, Now), "feed");
        File.Delete(Path.Combine(f.Root, "feed.xml"));
        File.WriteAllText(f.Post, "<script type=\"application/ld+json\">[]</script>");
        Throws(() => GenerateFeed.Generate(f.Root, Now), "Invalid JSON-LD");
        f.Metadata("2024-01-15");
        f.Archive(Entry.Replace("2024-01-15", "2024-01-16"));
        Throws(() => GenerateFeed.Generate(f.Root, Now), "metadata date does not match");

        using var blocked = new Fixture();
        string destination = Path.Combine(blocked.Root, "feed.xml");
        Directory.CreateDirectory(destination);
        string sentinel = Path.Combine(destination, "keep");
        File.WriteAllText(sentinel, "original");
        bool failed = false;
        try { GenerateFeed.WriteFeed(blocked.Root, original); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Assert(failed && File.ReadAllText(sentinel) == "original" &&
               !Directory.EnumerateFiles(blocked.Root, ".feed.xml.*.tmp").Any(),
            "A failed feed replacement did not preserve the destination and clean up");
    }

    private static void TestSync()
    {
        using var f = new Fixture();
        string baseCss = SyncInlineAssets.BaseStart + "\n        p { color: red; }\n" + SyncInlineAssets.BaseEnd;
        string css = SyncInlineAssets.CssStart + "\n        main { color: red; }\n" + SyncInlineAssets.CssEnd;
        string page = "<style>\n    </style>\n<body><pre tabindex=\"0\">code</pre>\n</body>";
        string updated = SyncInlineAssets.SyncPage(page, baseCss, css);
        Assert(updated.Contains(baseCss) && updated.Contains(css) && updated.Contains(SyncInlineAssets.CodeScrolling),
            "Sync did not insert shared assets");
        Assert(SyncInlineAssets.SyncPage(updated, baseCss, css) == updated, "Sync was not stable");
        Assert(SyncInlineAssets.SyncPage(updated, baseCss, css.Replace("red", "blue")).Contains("color: blue"),
            "Sync did not update shared styles");
        Assert(!SyncInlineAssets.SyncPage(updated.Replace("<pre tabindex=\"0\">code</pre>", ""), baseCss, css)
            .Contains(SyncInlineAssets.JsStart), "Sync did not remove unused code scrolling");
        File.WriteAllText(Path.Combine(f.Root, "index.html"), $"<style>\n{baseCss}\n{css}\n    </style>\n<body></body>");
        File.WriteAllText(Path.Combine(f.Root, "post.html"), page);
        File.WriteAllText(Path.Combine(f.Root, "sitemap.txt"), "https://thomasryan.dev/\nhttps://thomasryan.dev/post.html\n");
        Throws(() => SyncInlineAssets.Run(f.Root, true), "out of date");
        SyncInlineAssets.Run(f.Root, false);
        SyncInlineAssets.Run(f.Root, true);
        Assert(File.ReadAllText(Path.Combine(f.Root, "post.html")).Contains(SyncInlineAssets.CodeScrolling),
            "Sync command did not update the page");
        string post = Path.Combine(f.Root, "post.html");
        string snapshot = SyncInlineAssets.ReadPage(post);
        File.WriteAllText(post, snapshot + "\nConcurrent edit");
        Throws(() => SyncInlineAssets.WritePage(post, snapshot, updated), "changed during");
        Assert(SyncInlineAssets.ReadPage(post) == snapshot + "\nConcurrent edit",
            "Sync overwrote a concurrent edit");
        if (!OperatingSystem.IsWindows())
        {
            using var outside = new Fixture();
            Directory.CreateSymbolicLink(Path.Combine(f.Root, "outside"), outside.Root);
            File.WriteAllText(Path.Combine(f.Root, "sitemap.txt"), "https://thomasryan.dev/outside/2024/01/post.html\n");
            Throws(() => SyncInlineAssets.SitePages(f.Root), "Unexpected sitemap page");
        }
    }

    private static void TestCsp()
    {
        string page = "<!doctype html><html><head><script>window.test = 1;</script></head><body></body></html>";
        string updated = SyncCsp.Update(page, false);
        Assert(updated.Contains("http-equiv=\"Content-Security-Policy\"") &&
               updated.Contains("'sha256-") && !updated.Contains("'unsafe-eval'"),
            "Main-site CSP did not include a restrictive script hash");
        Assert(SyncCsp.Update(updated, false) == updated, "CSP synchronization was not stable");
        string speculative = SyncCsp.Update(page.Replace("</head>", "<script type=\"speculationrules\">{\"prefetch\":[]}</script></head>"), false);
        Assert(speculative.Contains("'inline-speculation-rules'") && speculative.Split("'sha256-").Length == 2,
            "Speculation rules were hashed or not allowed by the CSP");
        Assert(SyncCsp.Update(updated.Replace("window.test = 1", "window.test = 2"), false) !=
               updated.Replace("window.test = 1", "window.test = 2"), "Changed script did not invalidate its CSP hash");
        string demo = SyncCsp.Update(page, true);
        Assert(demo.Contains("'unsafe-eval'") && demo.Contains("worker-src 'self' blob:"),
            "Demo CSP omitted its map compatibility rules");
        Throws(() => SyncCsp.Update(page.Replace("<head>", "<head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'\">"), false),
            "Unmarked CSP");
        string directory = Directory.CreateTempSubdirectory("site-csp-").FullName;
        try
        {
            string path = Path.Combine(directory, "index.html");
            File.WriteAllText(path, page);
            Throws(() => SyncCsp.Run(directory, true), "out of date");
            SyncCsp.Run(directory, false);
            SyncCsp.Run(directory, true);
            string synced = File.ReadAllText(path);
            Assert(synced == updated, "CSP command did not update the page");
            File.WriteAllText(path, synced.Replace("window.test = 1", "window.test = 2"));
            Throws(() => SyncCsp.Run(directory, true), "out of date");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestSite(string root)
    {
        SyncCsp.Run(root, true);
        var paths = SyncInlineAssets.SitePages(root);
        var parser = new HtmlParser();
        var pages = paths.ToDictionary(path => path, path => parser.ParseDocument(File.ReadAllText(path, Encoding.UTF8)));
        var homepage = SyncInlineAssets.ReadPage(Path.Combine(root, "index.html"));
        string baseCss = SyncInlineAssets.Section(homepage, SyncInlineAssets.BaseStart, SyncInlineAssets.BaseEnd);
        string css = SyncInlineAssets.Section(homepage, SyncInlineAssets.CssStart, SyncInlineAssets.CssEnd);
        foreach (var (path, page) in pages)
        {
            var skip = page.QuerySelectorAll("a.skip-link");
            Assert(skip.Length == 1 && skip[0].GetAttribute("href") == "#main-content", $"{path}: invalid skip link");
            var mains = page.QuerySelectorAll("main");
            Assert(mains.Length == 1 && mains[0].Id == "main-content" && mains[0].GetAttribute("tabindex") == "-1",
                $"{path}: invalid main landmark");
            var navs = page.QuerySelectorAll("nav[aria-label='Primary']");
            Assert(navs.Length == 1 && !navs[0].ClassList.Contains("hidemobile"), $"{path}: invalid navigation");
            Assert(!page.QuerySelectorAll("script[src]").Any(script => script.GetAttribute("src")!.Contains("gist.github.com")),
                $"{path}: external gist script");
            foreach (var block in page.QuerySelectorAll("pre"))
                Assert(block.GetAttribute("tabindex") == "0" && block.GetAttribute("role") == "region" &&
                       !string.IsNullOrEmpty(block.GetAttribute("aria-label")), $"{path}: inaccessible code block");
            Assert(page.QuerySelectorAll("script[type='speculationrules']").Length == 1,
                $"{path}: missing speculation rules");
            string text = SyncInlineAssets.ReadPage(path);
            Assert(text.Contains("@view-transition"), $"{path}: missing view transition");
            Assert(SyncInlineAssets.SyncPage(text, baseCss, css) == text, $"{path}: stale inline assets");
            foreach (var node in page.QuerySelectorAll("a[href], img[src], script[src]"))
            {
                string reference = node.GetAttribute(node.LocalName == "a" ? "href" : "src")!;
                if (reference.StartsWith("//") || Regex.IsMatch(reference, @"^[A-Za-z][A-Za-z0-9+.-]*:"))
                    continue;
                string[] fragmentParts = reference.Split('#', 2);
                string relative = Uri.UnescapeDataString(fragmentParts[0].Split('?', 2)[0]);
                string target = relative.Length == 0 ? path : relative.StartsWith('/')
                    ? Path.GetFullPath(Path.Combine(root, relative.TrimStart('/')))
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
                string fromRoot = Path.GetRelativePath(root, target);
                Assert(!Path.IsPathRooted(fromRoot) && fromRoot != ".." &&
                       !fromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    $"{path}: reference escapes site: {reference}");
                Assert(File.Exists(target) || Directory.Exists(target), $"{path}: missing target: {reference}");
                if (fragmentParts.Length > 1 && pages.TryGetValue(target, out var targetPage))
                    Assert(targetPage.GetElementById(Uri.UnescapeDataString(fragmentParts[1])) is not null,
                        $"{path}: missing fragment: {reference}");
            }
        }
        Assert(File.ReadAllBytes(Path.Combine(root, "feed.xml")).SequenceEqual(GenerateFeed.Generate(root)), "Feed is stale");
        var feed = XDocument.Load(Path.Combine(root, "feed.xml")).Root!;
        var urls = feed.Elements(GenerateFeed.Atom + "entry").Select(entry => Field(entry, "id")!).ToList();
        var articles = File.ReadAllLines(Path.Combine(root, "sitemap.txt"))
            .Where(url => Regex.IsMatch(url, @"/\d{4}/\d{2}/[^/]+\.html$")).ToHashSet(StringComparer.Ordinal);
        Assert(urls.Count == urls.Distinct().Count() && articles.SetEquals(urls), "Feed does not match sitemap");
        var entries = feed.Elements(GenerateFeed.Atom + "entry").ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var current = new Uri(urls[i]);
            string path = Path.Combine(root, Uri.UnescapeDataString(current.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar));
            var navigation = pages[path].QuerySelectorAll("main nav[aria-label='More posts']");
            Assert(navigation.Length == 1, $"{path}: expected one article navigation");
            var links = navigation[0].QuerySelectorAll("a[href]");
            Assert(links.Length == (i == 0 || i == entries.Count - 1 ? 1 : 2),
                $"{path}: incorrect number of article links");
            foreach (var (rel, position, label) in new[] {
                ("prev", i - 1, "Newer post: "), ("next", i + 1, "Older post: ")
            })
            {
                var matching = links.Where(link => link.GetAttribute("rel") == rel).ToList();
                if (position < 0 || position >= entries.Count)
                {
                    Assert(matching.Count == 0, $"{path}: unexpected {rel} link");
                    continue;
                }
                Assert(matching.Count == 1 &&
                    Uri.UnescapeDataString(new Uri(current, matching[0].GetAttribute("href")).AbsoluteUri) ==
                    Uri.UnescapeDataString(new Uri(urls[position]).AbsoluteUri) &&
                    matching[0].TextContent == label + Field(entries[position], "title"),
                    $"{path}: incorrect {rel} link");
            }
        }
        var imageArticles = new (string[] Path, string[] Originals)[] {
            (["2025", "10", "ultimate-trunk-lighting-for-toyota-camry.html"],
                ["camrystock.webp", "camryLargerBulb.webp", "camryadaptert10.webp", "camryinstallfinal.webp", "camryfinal.webp"]),
            (["2025", "01", "memory-card-won't-seat-nikon-z7.html"],
                ["MemoryCardScrewAndTool.webp", "PaperclipInTheMemoryCardSlot.webp"]),
            (["2024", "01", "camry-rear-speaker-jbl.html"],
                ["2012CamryRearSpeakerBlank.webp", "2012CamryNewSpeakerInstalled.webp",
                 "2012CamryDamagedRearSpeaker.webp", "2012CamryTrimClip.webp"]),
            (["2022", "10", "macan-headlights-LED-replacement.html"],
                ["IMG_2947.JPG", "FoglightColorComparison.webp", "BlubComparison.webp"])
        };
        foreach (var article in imageArticles)
        {
            string path = Path.Combine([root, .. article.Path]);
            var images = pages[path].QuerySelectorAll("main img[src]");
            for (int i = 0; i < article.Originals.Length; i++)
            {
                string original = article.Originals[i];
                var matching = images.Where(image => image.ParentElement?.GetAttribute("href") == original).ToList();
                string display = Path.GetFileNameWithoutExtension(original) + "-display.webp";
                Assert(matching.Count == 1 && matching[0].GetAttribute("src") == display,
                    $"{path}: {original} does not link from its display image");
                Assert(int.TryParse(matching[0].GetAttribute("width"), out int width) && width > 0 &&
                       int.TryParse(matching[0].GetAttribute("height"), out int height) && height > 0 &&
                       (i == 0 || matching[0].GetAttribute("loading") == "lazy"),
                    $"{path}: {display} has invalid dimensions or lazy loading");
                string directory = Path.GetDirectoryName(path)!;
                Assert(new FileInfo(Path.Combine(directory, display)).Length < new FileInfo(Path.Combine(directory, original)).Length / 2,
                    $"{path}: {display} is not substantially smaller than its original");
            }
        }
        var codePage = pages[Path.Combine(root, "2019", "07", "dapper-dotnet-core-odbc-sqlserver-2000.html")];
        Assert(string.Concat(codePage.QuerySelectorAll("pre code").Select(node => node.TextContent))
            .Contains("QuerySingleOrDefault<StellentExciseDocument>(sql)"), "Generic type missing from code example");
    }

    private static string? Browser()
    {
        string? configured = Environment.GetEnvironmentVariable("CHROME_BIN");
        if (configured is not null)
        {
            if (!File.Exists(configured)) throw new InvalidOperationException($"CHROME_BIN does not exist: {configured}");
            return configured;
        }
        string[] names = OperatingSystem.IsWindows()
            ? ["chromium.exe", "chrome.exe", "msedge.exe"]
            : ["chromium", "chromium-browser", "google-chrome", "chrome", "msedge"];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        foreach (string candidate in new[] {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string Harness(IEnumerable<string> paths, bool dark) => """
        <!doctype html>
        <meta charset="utf-8">
        <title>Responsive site checks</title>
        <iframe id="site" title="Page under test"></iframe>
        <pre id="result">RUNNING</pre>
        <script>
        const paths = PATHS;
        const dark = DARK;
        const widths = [320, 375, 425, 680, 681, 1024];
        const frame = document.getElementById("site");
        const result = document.getElementById("result");
        function luminance(color) {
            const channels = color.match(/\d+/g).slice(0, 3).map(value => {
                const c = Number(value) / 255;
                return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
            });
            return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
        }
        async function run() {
            const failures = [];
            let checked = 0;
            let scrollable = 0;
            for (const path of paths) {
                await new Promise((resolve, reject) => {
                    frame.onload = resolve;
                    frame.onerror = () => reject(new Error("Could not load " + path));
                    frame.src = path;
                });
                for (const width of widths) {
                    frame.style.width = width + "px";
                    const doc = frame.contentDocument;
                    const viewport = doc.documentElement.clientWidth;
                    const actual = doc.documentElement.scrollWidth;
                    const nav = doc.querySelector('nav[aria-label="Primary"]');
                    const links = nav ? [...nav.querySelectorAll("a")] : [];
                    if (actual > viewport) {
                        failures.push(path + " at " + width + "px overflows to " + actual + "px");
                    }
                    if (links.length !== 3 || links.some(link => {
                        const rect = link.getBoundingClientRect();
                        return rect.width === 0 || rect.height === 0;
                    })) {
                        failures.push(path + " at " + width + "px has hidden navigation");
                    }
                    if (width === 320) {
                        if (doc.querySelector('script:not([src])')?.textContent.includes("www.clarity.ms/tag/")) {
                            if (typeof frame.contentWindow.clarity !== "function" ||
                                !doc.querySelector('script[src^="https://www.clarity.ms/tag/"]')) {
                                failures.push(path + " did not run its Clarity bootstrap");
                            }
                        }
                        const probe = doc.createElement("script");
                        probe.textContent = "window.__cspProbe = true;";
                        doc.head.appendChild(probe);
                        if (frame.contentWindow.__cspProbe) {
                            failures.push(path + " allowed an unauthorized inline script");
                        }
                        probe.remove();
                        if (dark) {
                            if (!frame.contentWindow.matchMedia("(prefers-color-scheme: dark)").matches) {
                                failures.push(path + " did not load in dark mode");
                            }
                            for (const link of doc.querySelectorAll("nav a")) {
                                let ancestor = link;
                                let background;
                                do {
                                    background = frame.contentWindow.getComputedStyle(ancestor).backgroundColor;
                                    ancestor = ancestor.parentElement;
                                } while (background === "rgba(0, 0, 0, 0)" && ancestor);
                                const foreground = frame.contentWindow.getComputedStyle(link).color;
                                const levels = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
                                const contrast = (levels[0] + 0.05) / (levels[1] + 0.05);
                                if (contrast < 3) {
                                    failures.push(path + " dark navigation contrast is " + contrast.toFixed(2));
                                }
                            }
                        }
                        if (path === "/posts.html") {
                            const input = doc.querySelector("#post-search-input");
                            const status = doc.querySelector("#post-search-status");
                            const groups = [...doc.querySelectorAll("main > article")];
                            const entries = groups.flatMap(group => [...group.querySelectorAll(":scope > a")]);
                            if (!input || !status || doc.querySelector("#post-search").hidden) {
                                failures.push(path + " did not enable archive search");
                            } else {
                                const filter = value => {
                                    input.value = value;
                                    input.dispatchEvent(new frame.contentWindow.Event("input", { bubbles: true }));
                                    return entries.filter(entry => !entry.hidden);
                                };
                                const external = filter("TRANSITIONING TO OUR NEW GIS OPEN DATA SERVICES");
                                if (external.length !== 1 || !external[0].href.startsWith("https://gisandyou.org/")) {
                                    failures.push(path + " did not find an external post case-insensitively");
                                }
                                if (path === "/demos.html") {
                                    const demoLinks = [...doc.querySelectorAll("main article a[href]")];
                                    if (demoLinks.length !== 11 || demoLinks.some(link => !link.textContent.trim())) {
                                        failures.push(path + " has missing or unnamed demo links");
                                    }
                                }
                                if (filter("november 7, 2018").length !== 1) {
                                    failures.push(path + " did not search dates");
                                }
                                if (filter("not-a-real-post-9cb89").length !== 0 ||
                                    status.textContent !== "No posts found." || groups.some(group => !group.hidden)) {
                                    failures.push(path + " did not show an empty state");
                                }
                                if (filter("").length !== entries.length || groups.some(group => group.hidden)) {
                                    failures.push(path + " did not restore all entries");
                                }
                            }
                        }
                        for (const image of doc.querySelectorAll("main img[width][height]")) {
                            const rect = image.getBoundingClientRect();
                            const expected = Number(image.getAttribute("width")) / Number(image.getAttribute("height"));
                            if (!rect.height || Math.abs(rect.width / rect.height - expected) > 0.01) {
                                failures.push(path + " has an image with an incorrect aspect ratio");
                            }
                            if (image.getAttribute("src").endsWith("-display.webp") &&
                                image.getAttribute("loading") !== "lazy" &&
                                (image.naturalWidth !== Number(image.getAttribute("width")) ||
                                 image.naturalHeight !== Number(image.getAttribute("height")))) {
                                failures.push(path + " has an invalid display image or dimensions: " + image.src);
                            }
                        }
                        const skip = doc.querySelector('a.skip-link');
                        if (!skip) {
                            failures.push(path + " has no skip link");
                        } else {
                            skip.focus();
                            const rect = skip.getBoundingClientRect();
                            if (doc.activeElement !== skip || rect.top < 0 || rect.bottom > 800) {
                                failures.push(path + " has an inaccessible skip link");
                            }
                        }
                        for (const code of doc.querySelectorAll("main pre")) {
                            if (code.scrollWidth <= code.clientWidth) continue;
                            scrollable++;
                            code.focus();
                            code.scrollLeft = 0;
                            code.dispatchEvent(new frame.contentWindow.KeyboardEvent("keydown", {
                                key: "ArrowRight", bubbles: true, cancelable: true
                            }));
                            if (doc.activeElement !== code || code.scrollLeft <= 0) {
                                failures.push(path + " has a code block that cannot scroll right");
                            }
                            code.dispatchEvent(new frame.contentWindow.KeyboardEvent("keydown", {
                                key: "ArrowLeft", bubbles: true, cancelable: true
                            }));
                            if (code.scrollLeft !== 0) {
                                failures.push(path + " has a code block that cannot scroll left");
                            }
                        }
                    }
                    checked++;
                }
            }
            const noScriptFrame = document.createElement("iframe");
            noScriptFrame.setAttribute("sandbox", "allow-same-origin");
            document.body.appendChild(noScriptFrame);
            await new Promise((resolve, reject) => {
                noScriptFrame.onload = resolve;
                noScriptFrame.onerror = () => reject(new Error("Could not load no-script archive"));
                noScriptFrame.src = "/posts.html";
            });
            const noScriptDoc = noScriptFrame.contentDocument;
            const noScriptGroups = [...noScriptDoc.querySelectorAll("main > article")];
            if (!noScriptDoc.querySelector("#post-search").hidden || noScriptGroups.length === 0 ||
                noScriptGroups.some(group => group.hidden || [...group.querySelectorAll(":scope > a")].some(link => link.hidden))) {
                failures.push("Archive entries are not available without JavaScript");
            }
            const mapFrame = document.createElement("iframe");
            document.body.appendChild(mapFrame);
            await new Promise((resolve, reject) => {
                mapFrame.onload = resolve;
                mapFrame.onerror = () => reject(new Error("Could not load ResuMap"));
                mapFrame.src = "/__resumap_test__";
            });
            const mapWindow = mapFrame.contentWindow;
            try {
                mapWindow.isElementOnScreen = id => id === "welcome";
                mapWindow.onscroll();
                if (mapWindow.activeChapterName !== "ballard" ||
                    !mapFrame.contentDocument.getElementById("ballard").classList.contains("active") ||
                    mapFrame.contentDocument.getElementById("welcome").classList.contains("active")) {
                    failures.push("ResuMap did not activate its first transition");
                }
                mapWindow.isElementOnScreen = id => id === "kitsap";
                mapWindow.onscroll();
                mapWindow.isElementOnScreen = id => id === "pnw";
                mapWindow.onscroll();
                const last = mapFrame.contentDocument.getElementById("pnw");
                if (mapWindow.activeChapterName !== "pnw" || !last.classList.contains("active") ||
                    !last.classList.contains("col-xs-12")) {
                    failures.push("ResuMap did not preserve the final chapter and its layout");
                }
            } catch (error) {
                failures.push("ResuMap scrolling failed: " + error);
            }
            result.textContent = JSON.stringify({checked, scrollable, failures});
        }
        run().catch(error => {
            result.textContent = JSON.stringify({error: String(error)});
        });
        </script>
        """.Replace("PATHS", JsonSerializer.Serialize(paths)).Replace("DARK", dark ? "true" : "false");

    private static async Task Serve(HttpListener listener, string root, Func<string> harness, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) when (stopping.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { break; }
            string path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
            byte[] content;
            if (path == "/__responsive_test__")
            {
                content = Encoding.UTF8.GetBytes(harness());
                context.Response.ContentType = "text/html; charset=utf-8";
            }
            else if (path == "/__resumap_test__")
            {
                string demo = await File.ReadAllTextAsync(Path.Combine(root, "demos", "resumap.html"));
                demo = demo.Replace("https://api.tiles.mapbox.com/mapbox-gl-js/v0.44.1/mapbox-gl.js", "/__mock_mapbox__.js")
                    .Replace("script-src ", "script-src 'self' ");
                content = Encoding.UTF8.GetBytes(demo);
                context.Response.ContentType = "text/html; charset=utf-8";
            }
            else if (path == "/__mock_mapbox__.js")
            {
                content = Encoding.UTF8.GetBytes("window.mapboxgl = { Map: function () { this.flyTo = function (chapter) { if (!chapter) throw new Error('Missing chapter'); }; } };");
                context.Response.ContentType = "application/javascript; charset=utf-8";
            }
            else
            {
                string target = Path.GetFullPath(Path.Combine(root, path.TrimStart('/')));
                string relative = Path.GetRelativePath(root, target);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(target))
                {
                    context.Response.StatusCode = 404;
                    content = [];
                }
                else
                {
                    content = await File.ReadAllBytesAsync(target);
                    if (Path.GetExtension(target).Equals(".html", StringComparison.OrdinalIgnoreCase))
                        // Headless virtual time cannot settle cross-document transitions between iframe loads.
                        content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("@view-transition", "@ignored-view-transition"));
                    context.Response.ContentType = Path.GetExtension(target).ToLowerInvariant() switch {
                        ".html" => "text/html; charset=utf-8",
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        _ => "application/octet-stream"
                    };
                }
            }
            context.Response.Headers.Add("Content-Security-Policy",
                "default-src 'self' data:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'none'");
            context.Response.ContentLength64 = content.Length;
            await context.Response.OutputStream.WriteAsync(content);
            context.Response.Close();
        }
    }

    private static async Task TestResponsive(string root)
    {
        string? browser = Browser();
        if (browser is null)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                throw new InvalidOperationException("Chrome, Chromium or Edge is required for responsive checks in CI");
            Console.WriteLine("Responsive checks skipped: Chrome, Chromium or Edge is not installed.");
            return;
        }
        var paths = SyncInlineAssets.SitePages(root)
            .Select(path => "/" + string.Join("/", Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString))).ToList();
        paths.Add("/demos.html");
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener { IgnoreWriteExceptions = true };
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string page = "";
        using var stopping = new CancellationTokenSource();
        Task server = Serve(listener, root, () => page, stopping.Token);
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                page = Harness(paths, dark);
                string profile = Directory.CreateTempSubdirectory("site-browser-").FullName;
                try
                {
                    var start = new ProcessStartInfo(browser) {
                        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                    };
                    foreach (string arg in new[] { "--headless", "--disable-gpu", "--no-first-run",
                                 "--no-proxy-server", $"--user-data-dir={profile}", "--virtual-time-budget=60000",
                                 "--dump-dom", $"http://127.0.0.1:{port}/__responsive_test__" })
                        start.ArgumentList.Add(arg);
                    if (dark) start.ArgumentList.Add("--force-dark-mode");
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start browser");
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        process.Kill(true);
                        throw new InvalidOperationException("Browser checks timed out");
                    }
                    string output = await stdout;
                    string errors = await stderr;
                    Assert(process.ExitCode == 0, $"Browser failed: {errors}");
                    var match = Regex.Match(output, "<pre id=\"result\">(.*?)</pre>", RegexOptions.Singleline);
                    Assert(match.Success, "Browser did not return a test result");
                    string resultText = WebUtility.HtmlDecode(match.Groups[1].Value);
                    Assert(resultText != "RUNNING", "Browser checks did not finish");
                    using var result = JsonDocument.Parse(resultText);
                    Assert(!result.RootElement.TryGetProperty("error", out var error), $"Browser error: {error}");
                    Assert(result.RootElement.GetProperty("checked").GetInt32() == paths.Count * 6, "Not all viewports were checked");
                    Assert(result.RootElement.GetProperty("scrollable").GetInt32() > 0, "No scrollable code blocks were checked");
                    var failures = result.RootElement.GetProperty("failures").EnumerateArray().Select(item => item.GetString()).ToList();
                    Assert(failures.Count == 0, "Responsive checks failed: " + string.Join("; ", failures));
                }
                finally { Directory.Delete(profile, true); }
            }
        }
        finally
        {
            stopping.Cancel();
            listener.Stop();
            await server;
        }
        Console.WriteLine("Responsive and dark-mode browser checks passed.");
    }
}

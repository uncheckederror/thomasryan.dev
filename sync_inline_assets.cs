using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

public static class SyncInlineAssets
{
    public const string BaseStart = "        /* Shared base styles: start */";
    public const string BaseEnd = "        /* Shared base styles: end */";
    public const string CssStart = "        /* Shared site styles: start */";
    public const string CssEnd = "        /* Shared site styles: end */";
    public const string JsStart = "    <!-- Shared code scrolling: start -->";
    public const string JsEnd = "    <!-- Shared code scrolling: end -->";
    public static readonly string CodeScrolling = """
            <!-- Shared code scrolling: start -->
            <script>
                document.addEventListener("keydown", function (event) {
                    const code = event.target;
                    if (!(code instanceof HTMLPreElement) || !code.hasAttribute("tabindex") ||
                        event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey ||
                        code.scrollWidth <= code.clientWidth) {
                        return;
                    }
                    if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
                        code.scrollLeft += event.key === "ArrowRight" ? 40 : -40;
                        event.preventDefault();
                    }
                });
            </script>
            <!-- Shared code scrolling: end -->
        """.Replace("\r\n", "\n");

    private static string SourceDirectory([CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: dotnet run sync_inline_assets.cs -- [--check]");
            Console.WriteLine("Synchronize shared inline site styles and code-scrolling behavior.");
            return 0;
        }
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--check"))
        {
            Console.Error.WriteLine("Usage: dotnet run sync_inline_assets.cs -- [--check]");
            return 2;
        }

        try
        {
            Run(SourceDirectory(), args.Length == 1);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Inline asset synchronization failed: {ex.Message}");
            return 1;
        }
    }

    public static string ReadPage(string path) =>
        File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Section(string text, string start, string end)
    {
        int first = text.IndexOf(start, StringComparison.Ordinal);
        int last = text.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last < first ||
            text.IndexOf(start, first + start.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(end, last + end.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected exactly one {start.Trim()} section");
        return text[first..(last + end.Length)];
    }

    private static string ResolveLinks(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string current = Path.GetPathRoot(fullPath)!;
        foreach (string part in fullPath[current.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(true);
            if (target is not null) current = target.FullName;
        }
        return current;
    }

    public static List<string> SitePages(string root)
    {
        root = ResolveLinks(root);
        var pages = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(root, "sitemap.txt"), Encoding.UTF8))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var url) ||
                url.Scheme != "https" || url.Authority != "thomasryan.dev")
                throw new InvalidOperationException($"Unexpected sitemap URL: {line}");
            string relative = Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            if (relative == "demos.html") continue;
            string path = ResolveLinks(Path.Combine(root, relative));
            string pathFromRoot = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(pathFromRoot) || pathFromRoot == ".." ||
                pathFromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !path.EndsWith(".html", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected sitemap page: {line}");
            pages.Add(path);
        }
        if (pages.Count == 0 || pages.Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != pages.Count)
            throw new InvalidOperationException("Sitemap has no pages or contains duplicates");
        return pages;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int position = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (position < 0) throw new InvalidOperationException($"Missing expected text: {oldValue}");
        return text[..position] + newValue + text[(position + oldValue.Length)..];
    }

    public static string SyncPage(string text, string baseCss, string css)
    {
        if (text.Contains(BaseStart) || text.Contains(BaseEnd))
            text = ReplaceFirst(text, Section(text, BaseStart, BaseEnd), baseCss);
        else
        {
            var styles = Regex.Matches(text, @"<style(?:\s[^>]*)?>\n");
            if (styles.Count != 1) throw new InvalidOperationException("Expected one inline style block");
            int position = styles[0].Index + styles[0].Length;
            text = text.Insert(position, baseCss + "\n\n");
        }

        if (text.Contains(CssStart) || text.Contains(CssEnd))
            text = ReplaceFirst(text, Section(text, CssStart, CssEnd), css);
        else
        {
            if (Regex.Matches(text, "    </style>").Count != 1)
                throw new InvalidOperationException("Expected one inline style block");
            text = ReplaceFirst(text, "    </style>", css + "\n    </style>");
        }

        bool hasCode = Regex.IsMatch(text, @"<pre\b");
        if (text.Contains(JsStart) || text.Contains(JsEnd))
            text = ReplaceFirst(text, Section(text, JsStart, JsEnd), hasCode ? CodeScrolling : "");
        else if (hasCode)
        {
            if (Regex.Matches(text, "</body>").Count != 1)
                throw new InvalidOperationException("Expected one closing body tag");
            text = ReplaceFirst(text, "</body>", CodeScrolling + "\n</body>");
        }
        return text;
    }

    public static void Run(string root, bool check)
    {
        string homepage = ReadPage(Path.Combine(root, "index.html"));
        string baseCss = Section(homepage, BaseStart, BaseEnd);
        string css = Section(homepage, CssStart, CssEnd);
        var changed = new List<(string Path, string Original, string Content)>();
        foreach (string path in SitePages(root))
        {
            string original = ReadPage(path);
            string updated = SyncPage(original, baseCss, css);
            if (updated != original) changed.Add((path, original, updated));
        }

        if (check && changed.Count > 0)
            throw new InvalidOperationException("Inline assets are out of date: " +
                string.Join(", ", changed.Select(item => Path.GetRelativePath(root, item.Path))));
        foreach (var item in changed)
            if (ReadPage(item.Path) != item.Original)
                throw new IOException($"Page changed during inline asset synchronization: {item.Path}");
        foreach (var item in changed)
            WritePage(item.Path, item.Original, item.Content);
        Console.WriteLine(changed.Count == 0 ? "Inline assets are up to date." : $"Updated {changed.Count} pages.");
    }

    public static void WritePage(string path, string original, string updated)
    {
        if (ReadPage(path) != original)
            throw new IOException($"Page changed during inline asset synchronization: {path}");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            if (ReadPage(path) != original)
                throw new IOException($"Page changed during inline asset synchronization: {path}");
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

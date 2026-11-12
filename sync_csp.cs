#:package AngleSharp@1.5.0
#:property NuGetAuditMode=all
#:property WarningsAsErrors=NU1900,NU1901,NU1902,NU1903,NU1904,NU1905
using AngleSharp.Html.Parser;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class SyncCsp
{
    private const string Start = "    <!-- Content security policy: start -->";
    private const string End = "    <!-- Content security policy: end -->";

    private static string SourceDirectory([CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: dotnet run sync_csp.cs -- [--check]");
            return 0;
        }
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--check"))
        {
            Console.Error.WriteLine("Usage: dotnet run sync_csp.cs -- [--check]");
            return 2;
        }
        try
        {
            Run(SourceDirectory(), args.Length == 1);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"CSP synchronization failed: {ex.Message}");
            return 1;
        }
    }

    public static List<string> Pages(string root)
    {
        var pages = Directory.GetFiles(root, "*.html").Where(path =>
            Path.GetFileName(path) != "googleec49fea55f2afb11.html").ToList();
        foreach (var directory in Directory.GetDirectories(root).Where(path =>
                     Regex.IsMatch(Path.GetFileName(path), @"^\d{4}$") || Path.GetFileName(path) == "demos"))
            pages.AddRange(Directory.GetFiles(directory, "*.html", SearchOption.AllDirectories));
        return pages.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    public static string Update(string text, bool demo)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var document = new HtmlParser().ParseDocument(text);
        var hashes = new SortedSet<string>(StringComparer.Ordinal);
        bool speculation = false;
        foreach (var script in document.QuerySelectorAll("script:not([src])"))
        {
            string type = script.GetAttribute("type") ?? "";
            if (type.Equals("application/ld+json", StringComparison.OrdinalIgnoreCase)) continue;
            if (type.Equals("speculationrules", StringComparison.OrdinalIgnoreCase))
            {
                speculation = true;
                continue;
            }
            if (type.Length != 0 && !type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("module", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected inline script type: {type}");
            hashes.Add("'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script.TextContent))) + "'");
        }
        string scripts = hashes.Count > 0 ? string.Join(" ", hashes) + " " : "";
        if (speculation) scripts += "'inline-speculation-rules' ";
        string policy = demo
            ? "default-src 'self' https: data: blob:; script-src " + scripts +
              "'unsafe-eval' https:; style-src 'self' 'unsafe-inline' https:; " +
              "img-src 'self' https: data: blob:; connect-src 'self' https:; " +
              "frame-src https:; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'self'"
            : "default-src 'self' https://*.clarity.ms https://c.bing.com; script-src " + scripts +
              "https://*.clarity.ms https://c.bing.com; style-src 'self' 'unsafe-inline'; " +
              "img-src 'self' data: https://*.clarity.ms https://c.bing.com; " +
              "connect-src 'self' https://*.clarity.ms https://c.bing.com; " +
              "object-src 'none'; base-uri 'none'; form-action 'self'; frame-src 'none'";
        string block = Start + "\n    <meta http-equiv=\"Content-Security-Policy\" content=\"" + policy + "\">\n" + End;
        int first = text.IndexOf(Start, StringComparison.Ordinal);
        int last = text.IndexOf(End, StringComparison.Ordinal);
        int policyCount = document.QuerySelectorAll("meta[http-equiv]")
            .Count(meta => string.Equals(meta.GetAttribute("http-equiv"), "Content-Security-Policy",
                StringComparison.OrdinalIgnoreCase));
        if (first >= 0 || last >= 0)
        {
            if (first < 0 || last < first || text.IndexOf(Start, first + Start.Length, StringComparison.Ordinal) >= 0 ||
                text.IndexOf(End, last + End.Length, StringComparison.Ordinal) >= 0 || policyCount != 1)
                throw new InvalidOperationException("Expected exactly one CSP section");
            string oldBlock = text[first..(last + End.Length)];
            if (!oldBlock.Contains("http-equiv=\"Content-Security-Policy\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected CSP meta tag inside the marked section");
            int head = text.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            int firstScript = text.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
            if (head < 0 || first < head || (firstScript >= 0 && first > firstScript))
                throw new InvalidOperationException("CSP section must be in the head before scripts");
            text = text[..first] + block + text[(last + End.Length)..];
        }
        else
        {
            if (policyCount != 0) throw new InvalidOperationException("Unmarked CSP meta tag found");
            var heads = Regex.Matches(text, @"<head\s*>", RegexOptions.IgnoreCase);
            if (heads.Count != 1) throw new InvalidOperationException("Expected one head element");
            int position = heads[0].Index + heads[0].Length;
            text = text.Insert(position, "\n" + block);
        }
        return text;
    }

    public static void Run(string root, bool check)
    {
        var changed = new List<(string Path, string Original, string Updated)>();
        foreach (string path in Pages(root))
        {
            string original = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n');
            bool demo = Path.GetRelativePath(root, path).StartsWith("demos" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            string updated = Update(original, demo);
            if (updated != original) changed.Add((path, original, updated));
        }
        if (check && changed.Count > 0)
            throw new InvalidOperationException("CSP is out of date: " +
                string.Join(", ", changed.Select(item => Path.GetRelativePath(root, item.Path))));
        foreach (var item in changed)
        {
            if (File.ReadAllText(item.Path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n') != item.Original)
                throw new IOException($"Page changed during CSP synchronization: {item.Path}");
            string temporary = Path.Combine(Path.GetDirectoryName(item.Path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, item.Updated, new UTF8Encoding(false));
                File.Move(temporary, item.Path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        Console.WriteLine(changed.Count == 0 ? "CSP is up to date." : $"Updated CSP on {changed.Count} pages.");
    }
}

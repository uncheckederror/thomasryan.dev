#:package AngleSharp@1.5.0
#:property NuGetAuditMode=all
#:property WarningsAsErrors=NU1900,NU1901,NU1902,NU1903,NU1904,NU1905
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

public static class GenerateFeed
{
    public const string Base = "https://thomasryan.dev/";
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static string SourceDirectory([CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: dotnet run generate_feed.cs -- [--check|--stdout]");
            Console.WriteLine("Generate the Atom feed from the local entries in posts.html.");
            return 0;
        }
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--check" && args[0] != "--stdout"))
        {
            Console.Error.WriteLine("Usage: dotnet run generate_feed.cs -- [--check|--stdout]");
            return 2;
        }

        try
        {
            string root = SourceDirectory();
            byte[] content = Generate(root);
            if (args.Length == 1 && args[0] == "--stdout")
                Console.OpenStandardOutput().Write(content);
            else if (args.Length == 1)
            {
                if (!File.ReadAllBytes(Path.Combine(root, "feed.xml")).SequenceEqual(content))
                    throw new InvalidOperationException("feed.xml is out of date; run dotnet run generate_feed.cs");
                Console.WriteLine("feed.xml is up to date.");
            }
            else
            {
                WriteFeed(root, content);
                Console.WriteLine("Updated feed.xml.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or JsonException or XmlException or
                                   TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Console.Error.WriteLine($"Feed generation failed: {ex.Message}");
            return 1;
        }
    }

    public static void WriteFeed(string root, byte[] content)
    {
        string destination = Path.Combine(root, "feed.xml");
        string temporary = Path.Combine(root, ".feed.xml." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static DateOnly ParseDate(string? value, string label)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException($"Invalid {label}: {value}");
        return date;
    }

    private static DateTimeOffset ParseTimestamp(string? value, string label)
    {
        if (value is null || !Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase) ||
            !DateTimeOffset.TryParse(value, Culture, DateTimeStyles.None, out var timestamp))
            throw new InvalidOperationException($"Invalid {label}; expected a timestamp with a time-zone offset: {value}");
        return timestamp;
    }

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        DateTime time = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(time, zone.GetUtcOffset(time));
    }

    private static DateTimeOffset InZone(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone);

    private static string Format(DateTimeOffset time, TimeZoneInfo zone) =>
        InZone(time, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Culture);

    private static (string Path, string Url)? LocalPostUrl(string href)
    {
        if (!Uri.TryCreate(new Uri(Base + "posts.html"), href, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host != "thomasryan.dev")
            return null;
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.EndsWith(".html", StringComparison.Ordinal)) return null;
        if (path.Contains('\\') || !Regex.IsMatch(path, @"^/\d{4}/\d{2}/[^/]+\.html$"))
            throw new InvalidOperationException($"Unexpected local post URL: {href}");
        string relative = path[1..];
        string url = Base + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
        return (relative, url);
    }

    private static string Text(string? value, string field, string href)
    {
        string text = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) throw new InvalidOperationException($"Missing {field} for {href}");
        return text;
    }

    private sealed record Post(string Url, string Title, string Summary, DateTimeOffset Published, DateTimeOffset Modified);
    private sealed record OldPost(string Title, string Summary, DateOnly PublishedDate, DateTimeOffset Updated);
    private sealed record OldFeed(DateTimeOffset Updated, Dictionary<string, OldPost> Entries);

    private static List<Post> ReadEntries(string root, TimeZoneInfo zone)
    {
        var parser = new HtmlParser();
        var archive = parser.ParseDocument(File.ReadAllText(Path.Combine(root, "posts.html"), Encoding.UTF8));
        var posts = new List<Post>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in archive.QuerySelectorAll("main a[href]"))
        {
            string href = link.GetAttribute("href")!;
            var local = LocalPostUrl(href);
            if (local is null) continue;
            if (!seen.Add(local.Value.Url)) throw new InvalidOperationException($"Duplicate post URL: {href}");
            string title = Text(link.QuerySelector("h3")?.TextContent, "title", href);
            string summary = Text(link.QuerySelector("p")?.TextContent, "summary", href);
            string? dateText = link.QuerySelector("time")?.GetAttribute("datetime");
            var published = Midnight(ParseDate(dateText, $"publication date for {href}"), zone);
            string postPath = Path.Combine(root, local.Value.Path.Replace('/', Path.DirectorySeparatorChar));
            var document = parser.ParseDocument(File.ReadAllText(postPath, Encoding.UTF8));
            var articles = new List<JsonElement>();
            foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
            {
                using var metadata = JsonDocument.Parse(script.TextContent);
                var item = metadata.RootElement;
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"Invalid JSON-LD metadata for {href}");
                if (item.TryGetProperty("@type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "BlogPosting")
                    articles.Add(item.Clone());
            }
            if (articles.Count != 1 || !articles[0].TryGetProperty("datePublished", out var articleDate) ||
                articleDate.ValueKind != JsonValueKind.String || articleDate.GetString() != dateText)
                throw new InvalidOperationException($"Article metadata date does not match archive for {href}");
            DateTimeOffset modified = published;
            if (articles[0].TryGetProperty("dateModified", out var modification))
            {
                if (modification.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"Invalid modification date for {href}");
                string value = modification.GetString()!;
                modified = Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")
                    ? Midnight(ParseDate(value, $"modification date for {href}"), zone)
                    : InZone(ParseTimestamp(value, $"modification date for {href}"), zone);
            }
            if (modified < published)
                throw new InvalidOperationException($"Modification date precedes publication date for {href}");
            posts.Add(new Post(local.Value.Url, title, summary, published, modified));
        }
        if (posts.Count == 0) throw new InvalidOperationException("No local posts found in posts.html");
        return posts.OrderByDescending(post => post.Published.Date).ToList();
    }

    private static OldFeed? ReadPrevious(string root, TimeZoneInfo zone)
    {
        string path = Path.Combine(root, "feed.xml");
        if (!File.Exists(path)) return null;
        var feed = XDocument.Load(path).Root;
        if (feed?.Name != Atom + "feed") throw new InvalidOperationException("Existing feed.xml is not an Atom feed");
        var updated = ParseTimestamp((string?)feed.Element(Atom + "updated"), "feed update timestamp");
        var entries = new Dictionary<string, OldPost>(StringComparer.Ordinal);
        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            string? id = (string?)entry.Element(Atom + "id");
            string? title = (string?)entry.Element(Atom + "title");
            string? summary = (string?)entry.Element(Atom + "summary");
            string? publishedText = (string?)entry.Element(Atom + "published");
            string? updatedText = (string?)entry.Element(Atom + "updated");
            if (string.IsNullOrEmpty(id) || entries.ContainsKey(id) || title is null || summary is null)
                throw new InvalidOperationException("Existing feed.xml contains a duplicate or incomplete entry");
            var published = ParseTimestamp(publishedText, $"publication timestamp for {id}");
            var modified = ParseTimestamp(updatedText, $"update timestamp for {id}");
            if (updatedText == publishedText && publishedText!.EndsWith("T00:00:00Z", StringComparison.Ordinal))
                modified = Midnight(DateOnly.FromDateTime(published.Date), zone);
            entries.Add(id, new OldPost(title, summary, DateOnly.FromDateTime(published.Date), InZone(modified, zone)));
        }
        return new OldFeed(InZone(updated, zone), entries);
    }

    public static byte[] Generate(string root, DateTimeOffset? currentTime = null)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var now = InZone(currentTime ?? DateTimeOffset.UtcNow, zone);
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var posts = ReadEntries(root, zone);
        var previous = ReadPrevious(root, zone);
        var postUpdates = new List<DateTimeOffset>();
        foreach (var post in posts)
        {
            var updated = post.Modified;
            if (previous?.Entries.TryGetValue(post.Url, out var old) == true)
            {
                if (old.Updated > updated) updated = old.Updated;
                if ((old.Title != post.Title || old.Summary != post.Summary ||
                     old.PublishedDate != DateOnly.FromDateTime(post.Published.Date)) && now > updated)
                    updated = now;
            }
            else if (previous is not null && now > updated) updated = now;
            postUpdates.Add(updated);
        }
        var feedUpdated = postUpdates.Max();
        if (previous is not null)
        {
            if (previous.Updated > feedUpdated) feedUpdated = previous.Updated;
            if (previous.Entries.Keys.Except(posts.Select(post => post.Url), StringComparer.Ordinal).Any() &&
                now > feedUpdated) feedUpdated = now;
        }

        var feed = new XElement(Atom + "feed",
            new XElement(Atom + "title", "Thomas August Ryan's Posts"),
            new XElement(Atom + "id", Base + "feed.xml"),
            new XElement(Atom + "link", new XAttribute("href", Base)),
            new XElement(Atom + "link", new XAttribute("rel", "self"),
                new XAttribute("type", "application/atom+xml"), new XAttribute("href", Base + "feed.xml")),
            new XElement(Atom + "updated", Format(feedUpdated, zone)),
            new XElement(Atom + "author", new XElement(Atom + "name", "Thomas August Ryan")));
        for (int i = 0; i < posts.Count; i++)
        {
            var post = posts[i];
            feed.Add(new XElement(Atom + "entry",
                new XElement(Atom + "title", post.Title),
                new XElement(Atom + "id", post.Url),
                new XElement(Atom + "link", new XAttribute("href", post.Url)),
                new XElement(Atom + "published", Format(post.Published, zone)),
                new XElement(Atom + "updated", Format(postUpdates[i], zone)),
                new XElement(Atom + "summary", post.Summary)));
        }
        string xml = "<?xml version='1.0' encoding='utf-8'?>\n" +
                     feed.ToString().Replace("\r\n", "\n") + "\n";
        return new UTF8Encoding(false).GetBytes(xml);
    }
}

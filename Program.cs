using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebExam;

public class PageInfo
{
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool Success { get; set; }
}

public class Program
{
    private static readonly HttpClient client = new HttpClient();
    private static readonly object logLock = new object();
    private static string pagesDir = "";
    private static string outputDir = "";
    private static string logFile = "";

    public static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        client.Timeout = TimeSpan.FromSeconds(30);

        Console.WriteLine("Welcome");

        List<string> urls = ReadUrls();
        if (urls.Count == 0)
        {
            Console.WriteLine("No valid URLs entered");
            return;
        }

        List<string> keywords = ReadKeywords();
        bool useApi = AskYesNo("Use API? (y/n)");

        outputDir = Path.Combine(AppContext.BaseDirectory, "Output");
        pagesDir = Path.Combine(outputDir, "Pages");
        Directory.CreateDirectory(pagesDir);
        logFile = Path.Combine(outputDir, "log.txt");

        Log("Download started");

        List<Task<PageInfo>> tasks = new List<Task<PageInfo>>();
        for (int i = 0; i < urls.Count; i++)
        {
            tasks.Add(DownloadPageAsync(urls[i], i + 1));
        }
        PageInfo[] pages = await Task.WhenAll(tasks);

        List<PageInfo> downloaded = pages.Where(p => p.Success).ToList();
        Log($"Download finished, {downloaded.Count} of {pages.Length} succeeded");

        Console.WriteLine();
        Console.WriteLine("Analysis results");
        await AnalyzePagesAsync(downloaded, keywords);

        //Additional task
        if (useApi)
        {
            Console.WriteLine();
            Console.WriteLine("External API metadata");
            await GetApiMetadataAsync(downloaded);
        }

        Console.WriteLine();
        Console.WriteLine("Processing completed");
        Console.WriteLine($"Files saved to: {outputDir}");
        Console.WriteLine("Press Enter to exit");
        Console.ReadLine();
    }

    private static List<string> ReadUrls()
    {
        List<string> urls = new List<string>();
        Console.WriteLine("Enter URLs, one per line");

        while (true)
        {
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            bool valid = Uri.TryCreate(line.Trim(), UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            if (valid)
            {
                urls.Add(line.Trim());
            }
            else
            {
                Console.WriteLine("Invalid URL, skipped");
            }
        }

        return urls;
    }

    private static List<string> ReadKeywords()
    {
        Console.WriteLine("Enter keywords separated by commas");
        string? line = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(line))
        {
            return new List<string> { "news", "contact", "privacy" };
        }

        return line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool AskYesNo(string question)
    {
        Console.WriteLine(question);
        string? answer = Console.ReadLine();
        return answer != null && answer.Trim().ToLower() == "y";
    }

    private static async Task<PageInfo> DownloadPageAsync(string url, int index)
    {
        PageInfo info = new PageInfo { Url = url };

        try
        {
            Console.WriteLine($"Downloading: {url}");
            string html = await client.GetStringAsync(url);

            string host = new Uri(url).Host.Replace(".", "_");
            string path = Path.Combine(pagesDir, $"page{index}_{host}.html");
            await File.WriteAllTextAsync(path, html);

            info.FilePath = path;
            info.Success = true;

            Log($"Saved: {url}");
            Console.WriteLine($"Downloaded: {url}");
        }
        catch (Exception ex)
        {
            Log($"Error: {url} - {ex.Message.TrimEnd('.')}");
            Console.WriteLine($"Failed: {url}");
        }

        return info;
    }

    private static async Task AnalyzePagesAsync(List<PageInfo> pages, List<string> keywords)
    {
        StringBuilder report = new StringBuilder();

        foreach (PageInfo page in pages)
        {
            string html = await File.ReadAllTextAsync(page.FilePath);
            long sizeBytes = new FileInfo(page.FilePath).Length;

            string text = GetPlainText(html);
            int wordCount = Regex.Matches(text, @"\w+").Count;

            report.AppendLine($"URL: {page.Url}");
            report.AppendLine($"Size: {sizeBytes / 1024.0:F1} KB");
            report.AppendLine($"Words: {wordCount}");

            foreach (string keyword in keywords)
            {
                int count = Regex.Matches(text, Regex.Escape(keyword), RegexOptions.IgnoreCase).Count;
                report.AppendLine($"Keyword \"{keyword}\": {count}");
            }

            report.AppendLine();
        }

        if (pages.Count == 0)
        {
            report.AppendLine("No pages to analyze");
        }

        Console.WriteLine(report.ToString());
        await File.WriteAllTextAsync(Path.Combine(outputDir, "results.txt"), report.ToString());
        Log("Analysis saved");
    }

    private static string GetPlainText(string html)
    {
        string text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(text);
    }

    private static async Task GetApiMetadataAsync(List<PageInfo> pages)
    {
        StringBuilder report = new StringBuilder();

        foreach (PageInfo page in pages)
        {
            report.AppendLine($"URL: {page.Url}");

            try
            {
                string apiUrl = "https://api.microlink.io/?url=" + Uri.EscapeDataString(page.Url);
                string json = await client.GetStringAsync(apiUrl);

                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement data = doc.RootElement.GetProperty("data");

                report.AppendLine($"Title: {ReadJsonText(data, "title")}");
                report.AppendLine($"Description: {ReadJsonText(data, "description")}");
                report.AppendLine($"Publisher: {ReadJsonText(data, "publisher")}");
                report.AppendLine($"Language: {ReadJsonText(data, "lang")}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"API error: {ex.Message.TrimEnd('.')}");
                Log($"API error: {page.Url}");
            }

            report.AppendLine();
        }

        Console.WriteLine(report.ToString());
        await File.WriteAllTextAsync(Path.Combine(outputDir, "metadata.txt"), report.ToString());
        Log("Metadata saved");
    }

    private static string ReadJsonText(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "Not available";
        }

        return "Not available";
    }

    private static void Log(string message)
    {
        lock (logLock)
        {
            File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
    }
}
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Jooble job search (https://jooble.org/api/about): one POST per search, key in the URL. Jooble aggregates many US job sites.
/// Results carry a short snippet rather than the full posting, and the link opens the job on Jooble.
/// </summary>
public sealed class JoobleClient(IHttpClientFactory factory, IOptions<JobFeedOptions> options, ILogger<JoobleClient> log)
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public async Task<List<FetchedJob>> SearchAsync(string query, string? location, bool remoteOnly, string datePosted, bool contractOnly, CancellationToken ct)
    {
        var o = options.Value;
        // Jooble has no remote filter: ask for remote in the keywords, then keep only jobs that say remote.
        var body = new JsonObject
        {
            ["keywords"] = remoteOnly && !Regex.IsMatch(query, @"\bremote\b", I) ? $"{query} remote" : query,
            ["page"] = "1",
            ["ResultOnPage"] = Math.Clamp(o.JoobleJobsPerSearch, 5, 50).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(location)) { body["location"] = location; body["radius"] = "50"; }
        if (datePosted switch { "today" => 0, "3days" => 3, "week" => 7, "month" => 30, _ => (int?)null } is int days)
            body["datecreatedfrom"] = DateTime.UtcNow.Date.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var req = new HttpRequestMessage(HttpMethod.Post, o.JoobleUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(o.JoobleApiKey))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage res;
        try { res = await factory.CreateClient("jooble").SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException(504, "JOBFEED_TIMEOUT", "Jooble search timed out", "Jooble took too long to answer. Try again in a minute.");
        }
        catch (HttpRequestException ex)
        {
            // The URL holds the key, so log only the error kind.
            log.LogWarning("Jooble unreachable: {Error}", ex.HttpRequestError);
            throw new AppException(503, "JOBFEED_UNAVAILABLE", "Jooble unavailable", "Couldn't reach Jooble. Check the server's internet connection and try again.");
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                log.LogError("Jooble rejected the key ({Status})", (int)res.StatusCode);
                throw new AppException(502, "JOBFEED_AUTH", "Jooble key rejected", "The Jooble API key was rejected. Ask your admin to check JobFeed:JoobleApiKey.");
            }
            if ((int)res.StatusCode == 429)
                throw new AppException(429, "JOBFEED_PROVIDER_LIMIT", "Jooble limit reached", "Jooble's request limit for this key has been reached. Try again later.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Jooble {Status}: {Body}", (int)res.StatusCode, Cut(text, 300));
                throw new AppException(502, "JOBFEED_ERROR", "Jooble search failed", "Jooble returned an error. Try again in a minute.");
            }
            JsonArray? jobs;
            try { jobs = JsonNode.Parse(text)?["jobs"] as JsonArray; }
            catch (System.Text.Json.JsonException)
            {
                log.LogWarning("Jooble returned invalid JSON: {Body}", Cut(text, 300));
                throw new AppException(502, "JOBFEED_ERROR", "Jooble search failed", "Jooble returned something unreadable. Try again in a minute.");
            }
            var found = (jobs ?? []).Select(Parse).OfType<FetchedJob>();
            if (remoteOnly) found = found.Where(j => j.Remote);
            if (contractOnly) found = found.Where(IsContract);
            return found.ToList();
        }
    }

    /// <summary>Jooble has no contract filter. Keep jobs typed as contract/temporary or untyped, plus any that mention contract terms.</summary>
    private static bool IsContract(FetchedJob j) =>
        j.EmploymentType is null || Regex.IsMatch(j.EmploymentType, @"contract|temporary|temp\b|freelance", I)
        || Regex.IsMatch($"{j.Title} {j.Description}", @"\bcontract|\bC2C\b|corp[- ]to[- ]corp|\bW-?2\b|\b1099\b", I);

    private static FetchedJob? Parse(JsonNode? j)
    {
        if (j is null) return null;
        var id = j["id"] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture) : S(j["id"]);
        var title = S(j["title"]);
        var link = S(j["link"]);
        if (id is null || id.Length > 200 || title is null || !SafeUrl(link)) return null;

        title = Text(title) ?? title;
        var location = S(j["location"]) ?? "";
        var description = Text(S(j["snippet"]));
        var remote = Regex.IsMatch($"{location} {title}", @"\bremote\b", I) || Regex.IsMatch(description ?? "", @"\b(fully|100%)\s+remote\b", I);
        var source = S(j["source"]);
        var (min, max) = DiceClient.Pay(Thousands(S(j["salary"])));
        var posted = S(j["updated"]) is { } p && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : (DateTime?)null;

        return new FetchedJob(id, Cut(title, 300), Cut(S(j["company"]) ?? "Confidential", 200), Cut(location.Length > 0 ? location : remote ? "Remote" : "", 200), remote,
            S(j["type"]) is { } t ? Cut(t, 40) : null, source is null ? "Jooble" : Cut(source, 100), link!, [new ApplyOption(source is null ? "Jooble" : Cut(source, 100), link!, false)],
            description is null ? null : Cut(description, 20000), min, max, posted, string.Join("\n", [title, description ?? ""]));
    }

    /// <summary>"$195k" → "$195000" so the pay parser sees a yearly figure.</summary>
    private static string? Thousands(string? s) => s is null ? null
        : Regex.Replace(s, @"(\d+(?:\.\d+)?)\s*k\b", m => (decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000).ToString("0", CultureInfo.InvariantCulture), I);

    /// <summary>Snippets are HTML fragments with &amp;nbsp; and bold tags.</summary>
    private static string? Text(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var t = Regex.Replace(html, @"<\s*br\b[^>]*>", "\n", I);
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\s*\n\s*(\n\s*)*", "\n").Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
    private static bool SafeUrl(string? url) =>
        url is { Length: <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
}

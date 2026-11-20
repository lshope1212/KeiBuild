using System.Globalization;
using System.Text.RegularExpressions;

namespace KeiBuild.Ingestion.Collection;

public static class RobotsRules
{
    public const string ProductToken = "KeiBuildIngestion";

    public static TimeSpan Check(string text, Uri target)
    {
        var groups = Parse(text);
        var applicable = groups.Where(group => group.Agents.Contains(ProductToken)).ToArray();
        if (applicable.Length == 0)
            applicable = groups.Where(group => group.Agents.Contains("*")).ToArray();

        var path = NormalizeOctets(target.PathAndQuery);
        Rule? bestMatch = null;
        foreach (var rule in applicable.SelectMany(group => group.Rules))
        {
            if (!rule.Matches(path))
                continue;
            if (bestMatch is null || rule.Specificity > bestMatch.Specificity ||
                (rule.Specificity == bestMatch.Specificity && rule.Allow))
                bestMatch = rule;
        }
        if (bestMatch is { Allow: false })
            throw new IngestionException("robots.txt disallows this URL for KeiBuildIngestion.") { RequiresSourceReview = true };

        var delay = TimeSpan.FromSeconds(1);
        foreach (var value in applicable.SelectMany(group => group.Delays))
        {
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds > 30)
                throw new IngestionException("robots.txt specifies an unsupported crawl delay. Use a permitted local document.");
            if (TimeSpan.FromSeconds(seconds) > delay)
                delay = TimeSpan.FromSeconds(seconds);
        }
        return delay;
    }

    private static List<Group> Parse(string text)
    {
        var groups = new List<Group>();
        Group? group = null;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Split('#', 2)[0];
            var separator = line.IndexOf(':');
            if (separator < 0)
                continue;

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            if (key == "user-agent" && value.Length > 0)
            {
                if (group is null || group.HasDirectives)
                {
                    group = new Group();
                    groups.Add(group);
                }
                group.Agents.Add(value);
                continue;
            }

            if (group is null)
                continue;
            if (key is "allow" or "disallow")
            {
                group.HasDirectives = true;
                if (value.StartsWith('/'))
                    group.Rules.Add(new Rule(key == "allow", NormalizeOctets(value)));
            }
            else if (key == "crawl-delay")
            {
                group.HasDirectives = true;
                group.Delays.Add(value);
            }
        }
        return groups;
    }

    // Compare UTF-8 octets, decoding escaped unreserved ASCII without treating an escaped slash as a slash.
    private static string NormalizeOctets(string value)
    {
        var builder = new System.Text.StringBuilder();
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        for (var i = 0; i < bytes.Length; i++)
        {
            var character = (char)bytes[i];
            if (character == '%' && i + 2 < bytes.Length &&
                byte.TryParse($"{(char)bytes[i + 1]}{(char)bytes[i + 2]}",
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var decoded))
            {
                var unreserved = (char)decoded;
                if (char.IsAsciiLetterOrDigit(unreserved) || unreserved is '-' or '.' or '_' or '~')
                    builder.Append(unreserved);
                else
                    builder.Append('%').Append(decoded.ToString("X2", CultureInfo.InvariantCulture));
                i += 2;
            }
            else if (bytes[i] > 127)
                builder.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            else
                builder.Append(character);
        }
        return builder.ToString();
    }

    private sealed class Group
    {
        public HashSet<string> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Rule> Rules { get; } = [];
        public List<string> Delays { get; } = [];
        public bool HasDirectives { get; set; }
    }

    private sealed record Rule(bool Allow, string Pattern)
    {
        public int Specificity => Pattern.Count(character => character != '*') - (Pattern.EndsWith('$') ? 1 : 0);

        public bool Matches(string path)
        {
            var anchored = Pattern.EndsWith('$');
            var body = anchored ? Pattern[..^1] : Pattern;
            if (!body.Contains('*'))
                return anchored ? path == body : path.StartsWith(body, StringComparison.Ordinal);
            if (body.Length > 8_192)
                throw new IngestionException("robots.txt contains a wildcard rule that is too large to evaluate safely.");

            var expression = "\\A" + Regex.Escape(body).Replace("\\*", ".*", StringComparison.Ordinal) +
                             (anchored ? "\\z" : "");
            try
            {
                return Regex.IsMatch(path, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch (RegexMatchTimeoutException error)
            {
                throw new IngestionException("robots.txt contains a rule that could not be evaluated safely.", error);
            }
        }
    }
}

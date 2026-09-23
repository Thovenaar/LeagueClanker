using System.Text.RegularExpressions;

namespace LeagueClanker.Core.Augments;

/// <summary>Turns League wiki markup into plain text: templates, links, bold/italic and HTML tags.</summary>
internal static partial class WikiText
{
    public static string Clean(string markup)
    {
        var text = markup;
        string previous;
        do
        {
            // Innermost templates first, until none are left.
            previous = text;
            text = TemplateRegex().Replace(text, m => Evaluate(m.Groups[1].Value.Trim(), m.Groups[2].Success ? m.Groups[2].Value : ""));
        }
        while (text != previous);

        text = FileLinkRegex().Replace(text, "");
        text = LinkRegex().Replace(text, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        text = ExternalLinkRegex().Replace(text, "$1");
        text = text.Replace("'''", "").Replace("''", "");
        text = BreakRegex().Replace(text, " ");
        text = HtmlTagRegex().Replace(text, "");
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string Evaluate(string name, string argText)
    {
        if (name.StartsWith('#'))
            return ""; // parser functions like {{#invoke:...}} pull data we don't have

        var args = argText.Split('|').Where(a => !NamedArgRegex().IsMatch(a)).Select(a => a.Trim()).ToList();
        var first = args.ElementAtOrDefault(0) ?? "";
        return name.ToLowerInvariant() switch
        {
            "tip" => args.ElementAtOrDefault(1) is { Length: > 0 } display ? display : first,
            "g" => $"{first} gold",
            _ => first,
        };
    }

    [GeneratedRegex(@"\{\{([^{}|]+)(?:\|([^{}]*))?\}\}")]
    private static partial Regex TemplateRegex();

    [GeneratedRegex(@"^\s*[\w ]+=")]
    private static partial Regex NamedArgRegex();

    [GeneratedRegex(@"\[\[(?:File|Image):[^\]]*\]\]|\b\d+px\|link=\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FileLinkRegex();

    [GeneratedRegex(@"\[\[([^\]|]*)(?:\|([^\]]*))?\]\]")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\[https?://\S+\s+([^\]]*)\]")]
    private static partial Regex ExternalLinkRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

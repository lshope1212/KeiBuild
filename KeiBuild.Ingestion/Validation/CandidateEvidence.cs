using System.Text.RegularExpressions;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Validation;

internal static partial class CandidateEvidence
{
    private const int MaxEvidenceLines = 8;
    private static readonly char[] ClauseSeparators = ['|', ';', ',', '。', '、'];

    internal static FitmentEntry Build(ClaimDraft? draft, NormalizedDocument document, SourceDefinition source,
        Uri url, CatalogImport import, int row)
    {
        if (draft is null || draft.Kind is null)
            throw Invalid(row, "a part or modification type is required.");
        if (draft.FirstLine < 1 || draft.LastLine > document.Lines.Count ||
            draft.LastLine < draft.FirstLine || draft.LastLine - draft.FirstLine >= MaxEvidenceLines ||
            draft.FitmentLine < draft.FirstLine || draft.FitmentLine > draft.LastLine)
            throw Invalid(row, "the evidence range or fitment line is outside the document or exceeds eight lines.");

        var lines = document.Lines.Skip(draft.FirstLine - 1).Take(draft.LastLine - draft.FirstLine + 1).ToArray();
        var excerpt = string.Join(' ', lines);
        var line = document.Lines[draft.FitmentLine - 1];
        var locator = draft.FirstLine == draft.LastLine
            ? $"Normalized line {draft.FirstLine}"
            : $"Normalized lines {draft.FirstLine}–{draft.LastLine} (fitment line {draft.FitmentLine})";
        var candidate = import.Validate(new FitmentEntry(
            draft.ChassisCode!, draft.Kind.Value, draft.Title!, draft.Manufacturer!, draft.Category!,
            excerpt, new SourceReference(source.Title, url.AbsoluteUri, source.Kind, locator, excerpt),
            draft.PartNumber, draft.OemReference), row);

        if (!SupportsChassis(line, candidate.ChassisCode) || lines.Any(item => ExplicitlyExcludes(item, candidate.ChassisCode)))
            throw Invalid(row, "the cited text does not explicitly support this chassis application.");
        if (!excerpt.Contains(candidate.Title, StringComparison.Ordinal) ||
            !excerpt.Contains(candidate.Manufacturer, StringComparison.Ordinal))
            throw Invalid(row, "the cited text does not contain the stated part name and manufacturer.");
        if (candidate.PartNumber is { } number &&
            (!HasToken(excerpt, number, numberToken: true) ||
             (PartNumberLabel().IsMatch(line) && !HasToken(line, number, numberToken: true))))
            throw Invalid(row, "the exact part number is missing from the cited application.");
        if (candidate.OemReference is { } reference &&
            !lines.SelectMany(item => item.Split(ClauseSeparators))
                .Any(clause => HasLabeledOemReference(clause, reference)))
            throw Invalid(row, "the exact OEM reference is missing from a labeled source field.");

        return candidate;
    }

    private static bool SupportsChassis(string line, string code) => line.Split(ClauseSeparators)
        .Any(clause => FitmentLabel().Matches(clause)
            .Any(match => HasToken(clause[(match.Index + match.Length)..], code, numberToken: false)) ||
            JapaneseFitmentSuffix().Matches(clause)
            .Any(match => HasToken(clause[..match.Index], code, numberToken: false)));

    private static bool ExplicitlyExcludes(string line, string code) => line.Split(ClauseSeparators)
        .Any(clause => StrongNegative().IsMatch(clause) && HasToken(clause, code, numberToken: false) ||
                       Exclusion().Matches(clause).Any(match => HasToken(clause[(match.Index + match.Length)..], code, numberToken: false)));

    private static bool HasToken(string text, string token, bool numberToken)
    {
        var comparison = numberToken ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var offset = 0;
        while ((offset = text.IndexOf(token, offset, comparison)) >= 0)
        {
            var end = offset + token.Length;
            if (!ContinuesIdentifier(text, offset - 1, -1, numberToken) &&
                !ContinuesIdentifier(text, end, 1, numberToken))
                return true;
            offset = end;
        }
        return false;
    }

    private static bool ContinuesIdentifier(string text, int index, int outward, bool partNumber)
    {
        if (index < 0 || index >= text.Length)
            return false;
        var value = text[index];
        var beyondDot = index + outward;
        return char.IsAsciiLetterOrDigit(value) ||
               (partNumber && (value is '_' or '-' or '/' ||
                (value == '.' && beyondDot >= 0 && beyondDot < text.Length && char.IsAsciiLetterOrDigit(text[beyondDot]))));
    }

    private static bool HasLabeledOemReference(string clause, string reference)
    {
        foreach (Match match in OemLabel().Matches(clause))
        {
            var value = clause[(match.Index + match.Length)..].TrimStart();
            if (value.StartsWith(reference, StringComparison.Ordinal) &&
                !ContinuesIdentifier(value, reference.Length, 1, partNumber: true))
                return true;
        }
        return false;
    }

    private static CatalogImportException Invalid(int row, string reason) => new($"Entry {row}: {reason}");

    [GeneratedRegex(@"\b(?:fits|compatible\s+with)\s+|\b(?:fitment|compatibility|applications?|(?:applicable\s+)?chassis(?:\s+code)?)\s*[:：]|(?:適合車種|適合型式|適合|対応車種|適用|車種型式|車両型式)\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FitmentLabel();

    [GeneratedRegex(@"に(?:適合|対応)(?:します|する)")]
    private static partial Regex JapaneseFitmentSuffix();

    [GeneratedRegex(@"\b(?:does(?:\s+not|n't)\s+fit|do\s+not\s+fit|not\s+(?:supported|compatible|applicable|a\s+fit|for)|incompatible|unsupported|unconfirmed|unverified|may\s+fit|might\s+fit|should\s+fit|fitment\s+unknown|unknown\s+fitment|verify\s+fitment)\b|適合不可|不適合|適合未確認|適合不明|非対応|対象外", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongNegative();

    [GeneratedRegex(@"\b(?:excludes?|excluding|except)\b|除く|除外", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Exclusion();

    [GeneratedRegex(@"(?<!OEM )(?<!OE )\b(?:part\s*(?:number|no\.?|#)|sku)\s*[:：]|(?<!純正)(?:品番|部品番号)\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartNumberLabel();

    [GeneratedRegex(@"\b(?:oem|oe)(?:\s+(?:reference|cross.reference|number|part\s+number|no\.?))?\s*[:：#]|(?:相当純正部番|純正品番|純正部番|純正部品番号)\s*[:：]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemLabel();
}

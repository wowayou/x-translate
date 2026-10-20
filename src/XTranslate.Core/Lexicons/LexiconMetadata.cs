namespace XTranslate.Core.Lexicons;

/// <summary>User-supplied attribution, not a verified licence or permission to redistribute. Empty means unknown.</summary>
public sealed record LexiconProvenance(string SourceName = "", string SourceUri = "", string License = "",
    string LicenseUri = "", string Attribution = "", string SourceVersion = "")
{
    public static LexiconProvenance Unspecified { get; } = new();

    public void Validate()
    {
        if (!Text(SourceName, 256) || !Text(SourceUri, 2048) || !Text(License, 256) ||
            !Text(LicenseUri, 2048) || !Text(Attribution, 4096, multiline: true) || !Text(SourceVersion, 120))
            throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
    }

    internal static bool Text(string? value, int limit, bool multiline = false) => value is not null &&
        value.Length <= limit && LexiconText.IsValid(value, multiline) && (multiline || !LexiconText.HasOuterWhitespace(value));
}

/// <summary>Local resource identity/revision. A source hash identifies captured input bytes, not their trustworthiness.</summary>
public sealed record LexiconRevision(string ResourceId, int Number, DateTimeOffset CreatedUtc,
    string SourceSha256 = "", string ChangeNote = "")
{
    public const int MaxNumber = 1_000_000;

    public void Validate()
    {
        if (!IsId(ResourceId) || Number is < 1 or > MaxNumber || CreatedUtc.Offset != TimeSpan.Zero ||
            CreatedUtc == default || SourceSha256 is null || SourceSha256.Length != 0 && !IsHash(SourceSha256) ||
            !LexiconProvenance.Text(ChangeNote, 2048, multiline: true))
            throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
    }

    public static bool IsId(string? value) => Hex(value, 32);
    public static bool IsHash(string? value) => Hex(value, 64);
    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Dictionary language direction is descriptive; it does not reverse or generate meanings.</summary>
public sealed record DictionaryInfo(string Name, string SourceLanguage, string TargetLanguage)
{
    public DictionaryInfo Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name) || !LexiconProvenance.Text(Name, 120))
            throw new LexiconImportException(LexiconErrorCode.InvalidDictionaryInfo);
        var source = TranslationLanguages.Find(SourceLanguage);
        var target = TranslationLanguages.Find(TargetLanguage);
        if (source is null || target is null) throw new LexiconImportException(LexiconErrorCode.InvalidLanguage);
        return this with { SourceLanguage = source.Code, TargetLanguage = target.Code };
    }
}

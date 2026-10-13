namespace XTranslate.Core;

public sealed record TranslationLanguage(string Code, string NativeName, string? DeepLCode, bool RightToLeft = false);

/// <summary>Stable target codes and conservative, explicit provider mappings. Not an engine guarantee.</summary>
public static class TranslationLanguages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } = Array.AsReadOnly(new[]
    {
        new TranslationLanguage("zh-CN", "简体中文", "ZH"),
        new TranslationLanguage("en", "English", "EN"),
        new TranslationLanguage("ja", "日本語", "JA"),
        new TranslationLanguage("ko", "한국어", "KO"),
        new TranslationLanguage("fr", "Français", "FR"),
        new TranslationLanguage("de", "Deutsch", "DE"),
        new TranslationLanguage("es", "Español", "ES"),
        new TranslationLanguage("it", "Italiano", "IT"),
        new TranslationLanguage("pt", "Português", "PT"),
        new TranslationLanguage("ru", "Русский", "RU"),
        new TranslationLanguage("nl", "Nederlands", "NL"),
        new TranslationLanguage("ar", "العربية", "AR", true),
        new TranslationLanguage("hi", "हिन्दी", null),
        new TranslationLanguage("th", "ไทย", null),
        new TranslationLanguage("vi", "Tiếng Việt", null),
        new TranslationLanguage("tr", "Türkçe", "TR"),
        new TranslationLanguage("uk", "Українська", "UK"),
        new TranslationLanguage("pl", "Polski", "PL"),
        new TranslationLanguage("id", "Bahasa Indonesia", "ID"),
        new TranslationLanguage("zh-TW", "繁體中文", null),
        new TranslationLanguage("da", "Dansk", "DA"),
        new TranslationLanguage("fi", "Suomi", "FI"),
        new TranslationLanguage("he", "עברית", null, true),
        new TranslationLanguage("ms", "Bahasa Melayu", null),
        new TranslationLanguage("nb", "Norsk bokmål", "NB"),
        new TranslationLanguage("sv", "Svenska", "SV"),
        new TranslationLanguage("cs", "Čeština", "CS"),
        new TranslationLanguage("el", "Ελληνικά", "EL"),
        new TranslationLanguage("ro", "Română", "RO"),
        new TranslationLanguage("hu", "Magyar", "HU"),
        new TranslationLanguage("sk", "Slovenčina", "SK"),
        new TranslationLanguage("bg", "Български", "BG"),
        new TranslationLanguage("bn", "বাংলা", null)
    });

    public static TranslationLanguage? Find(string? code) =>
        All.FirstOrDefault(language => language.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static bool Supports(ProviderKind provider, string? code) =>
        Find(code) is { } language && ProviderCatalog.IsAvailable(provider) &&
        (provider != ProviderKind.DeepL || language.DeepLCode is not null);

    public static string ForProvider(ProviderKind provider, string code)
    {
        if (!Supports(provider, code))
            throw new TranslationException("当前服务的内置语言目录未开放该目标语言，请手动选择语言或服务。", TranslationErrorCode.UnsupportedLanguage);
        var language = Find(code)!;
        return provider == ProviderKind.DeepL ? language.DeepLCode! : language.Code;
    }
}

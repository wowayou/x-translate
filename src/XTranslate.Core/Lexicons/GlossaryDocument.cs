namespace XTranslate.Core.Lexicons;

/// <summary>A complete data-only glossary snapshot. Attribution is supplied by the user, not verified permission.</summary>
public sealed class GlossaryDocument
{
    public Glossary Glossary { get; }
    public LexiconProvenance Provenance { get; }
    public LexiconRevision? Revision { get; }

    public GlossaryDocument(Glossary glossary, LexiconProvenance? provenance = null, LexiconRevision? revision = null)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        Provenance = provenance ?? LexiconProvenance.Unspecified;
        Provenance.Validate();
        revision?.Validate();
        Glossary = glossary;
        Revision = revision;
    }
}

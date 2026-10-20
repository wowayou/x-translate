using System.Runtime.CompilerServices;
using System.Text;

namespace XTranslate.Core.Lexicons;

/// <summary>
/// Strict CSV/TSV, streamed from a caller-owned input. UTF-8 with an optional UTF-8 BOM only.
/// Quoted fields may contain delimiters, doubled quotes and physical newlines. Whitespace is data.
/// Enumeration must complete before a caller commits any import; an early row is not a valid file.
/// </summary>
public static class DelimitedText
{
    private enum FieldState { Start, Unquoted, Quoted, AfterQuote }

    public static async IAsyncEnumerable<DelimitedTextRecord> ReadAsync(Stream input,
        DelimitedTextFormat format = DelimitedTextFormat.Csv, DelimitedTextLimits? limits = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("A readable input stream is required.", nameof(input));
        var delimiter = format switch
        {
            DelimitedTextFormat.Csv => ',',
            DelimitedTextFormat.Tsv => '\t',
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        limits ??= new DelimitedTextLimits();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        // Disable encoding auto-detection: accepting a UTF-16 BOM would bypass the promised UTF-8 format.
        using var reader = new StreamReader(new BoundedInput(input, limits.MaxBytes), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
        var buffer = new char[4096];
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = FieldState.Start;
        var recordNumber = 1;
        var lineNumber = 1;
        var recordStartLine = 1;
        var recordChars = 0;
        var firstCharacter = true;
        var recordStarted = false;
        var skipLf = false;
        var previousCr = false;

        LexiconImportException Error(LexiconErrorCode code, int? line = null) =>
            new(code, recordNumber, line ?? lineNumber, fields.Count + 1);
        void Append(char value, int currentLine)
        {
            if (field.Length >= limits.MaxFieldChars) throw Error(LexiconErrorCode.FieldTooLong, currentLine);
            field.Append(value);
        }
        void EndField()
        {
            if (fields.Count >= limits.MaxColumns) throw Error(LexiconErrorCode.TooManyColumns);
            fields.Add(field.ToString());
            field.Clear();
            state = FieldState.Start;
        }
        DelimitedTextRecord EndRecord()
        {
            // A consumer may cancel between two yields while both rows are in the same read buffer.
            cancellationToken.ThrowIfCancellationRequested();
            if (recordNumber > limits.MaxRecords) throw Error(LexiconErrorCode.TooManyRecords);
            EndField();
            var result = new DelimitedTextRecord(recordNumber++, recordStartLine, fields.ToArray());
            fields.Clear();
            recordChars = 0;
            recordStarted = false;
            return result;
        }

        while (true)
        {
            var count = await ReadBlockAsync(reader, buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[index];
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (value == '\uFEFF') continue;
                }
                var currentLine = lineNumber;
                if (value == '\r' || value == '\n' && !previousCr) lineNumber++;
                previousCr = value == '\r';
                if (skipLf)
                {
                    skipLf = false;
                    if (value == '\n') { recordStartLine = lineNumber; continue; }
                }
                if (char.IsControl(value) && value is not ('\r' or '\n' or '\t'))
                    throw Error(LexiconErrorCode.InvalidCharacter, currentLine);
                if (recordNumber > limits.MaxRecords) throw Error(LexiconErrorCode.TooManyRecords, currentLine);

                if (state != FieldState.Quoted && (value is '\r' or '\n'))
                {
                    var result = EndRecord();
                    recordStartLine = lineNumber;
                    skipLf = value == '\r';
                    yield return result;
                    continue;
                }
                recordStarted = true;
                if (recordChars >= limits.MaxRecordChars) throw Error(LexiconErrorCode.RecordTooLong, currentLine);
                recordChars++;
                if (state == FieldState.Quoted)
                {
                    if (value == '"') state = FieldState.AfterQuote;
                    else Append(value, currentLine);
                }
                else if (value == delimiter)
                {
                    EndField();
                    // Even an empty next field counts as a column; fail before buffering it.
                    if (fields.Count >= limits.MaxColumns) throw Error(LexiconErrorCode.TooManyColumns, currentLine);
                }
                else if (value == '"')
                {
                    if (state == FieldState.Start) state = FieldState.Quoted;
                    else if (state == FieldState.AfterQuote) { Append('"', currentLine); state = FieldState.Quoted; }
                    else throw Error(LexiconErrorCode.MalformedDelimitedText, currentLine);
                }
                else
                {
                    if (state == FieldState.AfterQuote) throw Error(LexiconErrorCode.MalformedDelimitedText, currentLine);
                    Append(value, currentLine);
                    state = FieldState.Unquoted;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (state == FieldState.Quoted) throw Error(LexiconErrorCode.MalformedDelimitedText);
        if (recordStarted) yield return EndRecord();
    }

    private static async ValueTask<int> ReadBlockAsync(StreamReader reader, char[] buffer, CancellationToken token)
    {
        try { return await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); }
        catch (DecoderFallbackException)
        {
            // Decoder messages may contain input bytes. StreamReader can read ahead across records,
            // so neither its message nor a guessed record/line location is safe to expose.
            throw new LexiconImportException(LexiconErrorCode.InvalidEncoding);
        }
    }

    /// <summary>Reads at most the allowance plus one sentinel byte; disposing does not own the underlying stream.</summary>
    private sealed class BoundedInput(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Allowance(int count) => (int)Math.Min(count, limit - _read + 1);
        private int Count(int count)
        {
            _read += count;
            if (_read > limit) throw new LexiconImportException(LexiconErrorCode.InputTooLarge);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowance(count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowance(buffer.Length)], cancellationToken).ConfigureAwait(false));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

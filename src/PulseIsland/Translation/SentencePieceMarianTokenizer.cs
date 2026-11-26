using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PulseIsland.Translation;

/// <summary>
/// Pure C# SentencePiece (Unigram Viterbi over .spm protobuf) + HuggingFace MarianMT vocab.json tokenizer.
/// Requires zero external NuGet packages and accurately handles MarianMT shared source/target vocabularies.
/// </summary>
internal sealed class SentencePieceMarianTokenizer
{
    private const char MetaSpace = '\u2581';

    private readonly Dictionary<string, float> _pieceScores;
    private readonly int _maxPieceLen;
    private readonly Dictionary<string, long> _vocab;
    private readonly Dictionary<long, string> _idToPiece;

    public long DecoderStartTokenId { get; }
    public long EosTokenId { get; }
    public long PadTokenId { get; }
    public long UnkTokenId { get; }

    private SentencePieceMarianTokenizer(
        Dictionary<string, float> pieceScores,
        int maxPieceLen,
        Dictionary<string, long> vocab,
        Dictionary<long, string> idToPiece,
        long decoderStartTokenId,
        long eosTokenId,
        long padTokenId,
        long unkTokenId)
    {
        _pieceScores = pieceScores;
        _maxPieceLen = maxPieceLen;
        _vocab = vocab;
        _idToPiece = idToPiece;
        DecoderStartTokenId = decoderStartTokenId;
        EosTokenId = eosTokenId;
        PadTokenId = padTokenId;
        UnkTokenId = unkTokenId;
    }

    public static SentencePieceMarianTokenizer Load(string modelDirectory)
    {
        string spmPath = Path.Combine(modelDirectory, "source.spm");
        string vocabPath = Path.Combine(modelDirectory, "vocab.json");
        string configPath = Path.Combine(modelDirectory, "config.json");

        if (!File.Exists(spmPath))
            throw new FileNotFoundException($"Missing tokenizer file: {spmPath}");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Missing vocabulary file: {vocabPath}");

        var (pieceScores, maxLen) = ParseSentencePieceProto(spmPath);
        var (vocab, idToPiece) = LoadVocabJson(vocabPath);

        long unkId = vocab.TryGetValue("<unk>", out long u) ? u : 1;
        long eosId = vocab.TryGetValue("</s>", out long e) ? e : 0;
        long padId = vocab.TryGetValue("<pad>", out long p) ? p : 62517;
        long bosId = padId;

        if (File.Exists(configPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("eos_token_id", out var eosEl) && eosEl.TryGetInt64(out long cfgEos))
                eosId = cfgEos;
            if (root.TryGetProperty("pad_token_id", out var padEl) && padEl.TryGetInt64(out long cfgPad))
                padId = cfgPad;
            if (root.TryGetProperty("decoder_start_token_id", out var bosEl) && bosEl.TryGetInt64(out long cfgBos))
                bosId = cfgBos;
            else
                bosId = padId;
        }

        return new SentencePieceMarianTokenizer(pieceScores, maxLen, vocab, idToPiece, bosId, eosId, padId, unkId);
    }

    public long[] Encode(string rawText)
    {
        string preprocessed = PreprocessInputText(rawText);
        if (string.IsNullOrEmpty(preprocessed))
            return [EosTokenId];

        string normalized = preprocessed.Normalize(NormalizationForm.FormKC);
        normalized = MetaSpace + Regex.Replace(normalized.Trim(), @"\s+", MetaSpace.ToString());

        int n = normalized.Length;
        var bestScore = new double[n + 1];
        var backStart = new int[n + 1];
        var backPiece = new string[n + 1];

        for (int i = 1; i <= n; i++)
            bestScore[i] = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            if (double.IsNegativeInfinity(bestScore[i]))
                continue;

            int maxEnd = Math.Min(n, i + _maxPieceLen);
            bool matchedAny = false;
            for (int j = i + 1; j <= maxEnd; j++)
            {
                string sub = normalized.Substring(i, j - i);
                if (_pieceScores.TryGetValue(sub, out float score))
                {
                    double cand = bestScore[i] + score;
                    if (cand > bestScore[j])
                    {
                        bestScore[j] = cand;
                        backStart[j] = i;
                        backPiece[j] = sub;
                    }
                    matchedAny = true;
                }
            }

            // Always allow single-char fallback so unseen symbols never stall Viterbi
            if (!matchedAny || double.IsNegativeInfinity(bestScore[i + 1]))
            {
                double cand = bestScore[i] - 30.0;
                if (cand > bestScore[i + 1])
                {
                    bestScore[i + 1] = cand;
                    backStart[i + 1] = i;
                    backPiece[i + 1] = normalized.Substring(i, 1);
                }
            }
        }

        var pieces = new List<string>();
        int pos = n;
        while (pos > 0)
        {
            pieces.Add(backPiece[pos]);
            pos = backStart[pos];
        }
        pieces.Reverse();

        var ids = new List<long>(pieces.Count + 1);
        foreach (string piece in pieces)
        {
            if (_vocab.TryGetValue(piece, out long id))
            {
                ids.Add(id);
            }
            else if (piece.Length > 1)
            {
                foreach (char ch in piece)
                {
                    if (_vocab.TryGetValue(ch.ToString(), out long cid))
                        ids.Add(cid);
                    else if (ch != MetaSpace)
                        ids.Add(UnkTokenId);
                }
            }
            else if (piece != MetaSpace.ToString())
            {
                ids.Add(UnkTokenId);
            }
        }

        ids.Add(EosTokenId);
        return ids.ToArray();
    }

    public string Decode(IReadOnlyList<long> ids)
    {
        var sb = new StringBuilder();
        foreach (long id in ids)
        {
            if (id == DecoderStartTokenId || id == EosTokenId || id == PadTokenId || id == UnkTokenId)
                continue;

            if (_idToPiece.TryGetValue(id, out string? piece))
            {
                if (piece is "</s>" or "<s>" or "<pad>" or "<unk>")
                    continue;
                sb.Append(piece);
            }
        }

        string text = sb.ToString().Replace(MetaSpace, ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"\s+([,.!?:;])", "$1");
        return text;
    }

    internal static string PreprocessInputText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string s = input.Trim()
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u02BC', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"');
        s = Regex.Replace(s, @"\*{2,}", string.Empty);
        s = Regex.Replace(s, @"[!?]{2,}", m => m.Value.Contains('?') ? "?" : "!");

        // Expand common casual English abbreviations & contractions missing apostrophes
        // so compact MarianMT models (e.g. opus-mt-en-ru) never truncate or mistranslate memes/chats/subtitles.
        s = ExpandCasualEnglishForNmt(s);

        int letters = 0;
        int uppers = 0;
        int lowers = 0;
        foreach (char ch in s)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                if (char.IsUpper(ch)) uppers++;
                else if (char.IsLower(ch)) lowers++;
            }
        }

        // Opus-MT is cased; ALL-CAPS text (e.g., shouts/memes/UI headers) fragments badly into single chars,
        // and all-lowercase casual sentences also translate significantly more accurately with sentence-initial capitalization.
        if (letters >= 4 && uppers * 4 >= letters * 3)
        {
            s = CapitalizeFirstLetter(s.ToLowerInvariant());
        }
        else if (letters >= 4 && uppers == 0 && lowers == letters)
        {
            s = CapitalizeFirstLetter(s);
        }

        return s.Trim();
    }

    private static string CapitalizeFirstLetter(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                break;
            }
        }
        return new string(chars);
    }

    private static string ExpandCasualEnglishForNmt(string s)
    {
        // Only apply when text contains Latin letters
        if (!s.Any(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
            return s;

        s = Regex.Replace(s, @"\bw/o\b", "without", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bw/", "with ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\byrs\b", "years", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\byr\b", "year", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bhrs\b", "hours", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bmins\b", "minutes", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bsecs\b", "seconds", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bur\b", "your", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bu\b", "you", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bim\b", "I'm", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bive\b", "I've", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bdont\b", "don't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bcant\b", "can't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bwont\b", "won't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bdidnt\b", "didn't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bisnt\b", "isn't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\barent\b", "aren't", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bidk\b", "I don't know", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\btbh\b", "to be honest", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bngl\b", "not going to lie", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\brn\b", "right now", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bpls\b", "please", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bthx\b", "thanks", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bbtw\b", "by the way", RegexOptions.IgnoreCase);
        return s;
    }

    private static (Dictionary<string, long> Vocab, Dictionary<long, string> IdToPiece) LoadVocabJson(string vocabPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(vocabPath, Encoding.UTF8));
        var vocab = new Dictionary<string, long>(StringComparer.Ordinal);
        var idToPiece = new Dictionary<long, string>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.TryGetInt64(out long id))
            {
                vocab[prop.Name] = id;
                idToPiece[id] = prop.Name;
            }
        }

        return (vocab, idToPiece);
    }

    private static (Dictionary<string, float> Scores, int MaxLen) ParseSentencePieceProto(string spmPath)
    {
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        int maxLen = 1;

        byte[] data = File.ReadAllBytes(spmPath);
        int offset = 0;

        while (offset < data.Length)
        {
            ulong tag = ReadVarint(data, ref offset);
            int field = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (wireType == 2)
            {
                int len = (int)ReadVarint(data, ref offset);
                int end = Math.Min(data.Length, offset + len);
                if (field == 1) // ModelProto.pieces
                {
                    string piece = string.Empty;
                    float score = -10.0f;
                    int type = 1;

                    int pOff = offset;
                    while (pOff < end)
                    {
                        ulong pTag = ReadVarint(data, ref pOff);
                        int pField = (int)(pTag >> 3);
                        int pWire = (int)(pTag & 0x7);

                        if (pField == 1 && pWire == 2)
                        {
                            int sLen = (int)ReadVarint(data, ref pOff);
                            piece = Encoding.UTF8.GetString(data, pOff, Math.Min(sLen, end - pOff));
                            pOff += sLen;
                        }
                        else if (pField == 2 && pWire == 5)
                        {
                            score = BitConverter.ToSingle(data, pOff);
                            pOff += 4;
                        }
                        else if (pField == 3 && pWire == 0)
                        {
                            type = (int)ReadVarint(data, ref pOff);
                        }
                        else
                        {
                            SkipField(data, pWire, ref pOff);
                        }
                    }

                    // type == 1 (NORMAL) or type == 4 (USER_DEFINED)
                    if (!string.IsNullOrEmpty(piece) && type is 1 or 4)
                    {
                        scores[piece] = score;
                        if (piece.Length > maxLen)
                            maxLen = piece.Length;
                    }
                }
                offset = end;
            }
            else
            {
                SkipField(data, wireType, ref offset);
            }
        }

        return (scores, Math.Max(1, maxLen));
    }

    private static ulong ReadVarint(byte[] data, ref int offset)
    {
        ulong result = 0;
        int shift = 0;
        while (offset < data.Length)
        {
            byte b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
        return result;
    }

    private static void SkipField(byte[] data, int wireType, ref int offset)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(data, ref offset);
                break;
            case 1:
                offset += 8;
                break;
            case 2:
                int len = (int)ReadVarint(data, ref offset);
                offset += len;
                break;
            case 5:
                offset += 4;
                break;
            default:
                offset = data.Length;
                break;
        }
    }
}

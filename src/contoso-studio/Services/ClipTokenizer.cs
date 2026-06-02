using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoStudio.Services;

/// <summary>
/// CLIP byte-pair-encoding tokenizer (port of openai/CLIP/clip/simple_tokenizer.py).
/// Loads <c>vocab.json</c> + <c>merges.txt</c> bundled under
/// <c>Assets/Models/clip-vit-base-patch16/tokenizer/</c> and produces the int32 input_ids /
/// attention_mask tensors expected by the CLIP text encoder (fixed length 77, BOS=49406, EOS=49407).
/// Pure C# — no native dependency.
/// </summary>
public sealed class ClipTokenizer
{
    public const int MaxLen = 77;
    public const int BosToken = 49406;
    public const int EosToken = 49407;

    private static readonly Regex Pat = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Whitespace cleanup matching CLIP's `whitespace_clean`.
    private static readonly Regex WhitespaceClean = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _bpeRanks;
    private readonly Dictionary<int, char> _byteEncoder;
    private readonly Dictionary<string, string> _cache = new();

    private ClipTokenizer(Dictionary<string, int> encoder, Dictionary<(string, string), int> bpeRanks)
    {
        _encoder = encoder;
        _bpeRanks = bpeRanks;
        _byteEncoder = BytesToUnicode();
    }

    public static ClipTokenizer LoadBundled()
    {
        var dir = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!,
            "Assets", "Models", "clip-vit-base-patch16", "tokenizer");
        var vocabPath = Path.Combine(dir, "vocab.json");
        var mergesPath = Path.Combine(dir, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
            throw new FileNotFoundException($"CLIP tokenizer files missing under {dir}");

        using var fs = File.OpenRead(vocabPath);
        var encoder = JsonSerializer.Deserialize<Dictionary<string, int>>(fs)
            ?? throw new InvalidDataException("vocab.json is empty");

        var lines = File.ReadAllLines(mergesPath);
        // First line is the HF header "#version: 0.2"; skip it. Real merges start at index 1.
        // The OpenAI tokenizer uses 48,894 merges (lines 1..48894).
        var bpeRanks = new Dictionary<(string, string), int>();
        for (int i = 1; i < lines.Length && i <= 48894; i++)
        {
            var parts = lines[i].Split(' ');
            if (parts.Length != 2) continue;
            bpeRanks[(parts[0], parts[1])] = i - 1;
        }

        return new ClipTokenizer(encoder, bpeRanks);
    }

    /// <summary>Reversible mapping from bytes [0..255] to printable unicode chars (GPT-2 / CLIP scheme).</summary>
    private static Dictionary<int, char> BytesToUnicode()
    {
        var bs = new List<int>();
        for (int b = '!'; b <= '~'; b++) bs.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);
        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }
        var map = new Dictionary<int, char>();
        for (int i = 0; i < bs.Count; i++)
            map[bs[i]] = (char)cs[i];
        return map;
    }

    private static HashSet<(string, string)> GetPairs(IList<string> word)
    {
        var pairs = new HashSet<(string, string)>();
        for (int i = 0; i < word.Count - 1; i++)
            pairs.Add((word[i], word[i + 1]));
        return pairs;
    }

    private string Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;

        // Initial word: each character as its own symbol; last char carries the "</w>" end-of-word marker.
        var word = new List<string>(token.Length);
        for (int i = 0; i < token.Length - 1; i++) word.Add(token[i].ToString());
        word.Add(token[^1] + "</w>");

        var pairs = GetPairs(word);
        if (pairs.Count == 0)
        {
            var solo = token + "</w>";
            _cache[token] = solo;
            return solo;
        }

        while (true)
        {
            (string, string)? bestPair = null;
            int bestRank = int.MaxValue;
            foreach (var p in pairs)
            {
                if (_bpeRanks.TryGetValue(p, out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestPair = p;
                }
            }
            if (bestPair == null) break;

            var (first, second) = bestPair.Value;
            var newWord = new List<string>();
            int i = 0;
            while (i < word.Count)
            {
                int j = word.IndexOf(first, i);
                if (j < 0)
                {
                    for (int k = i; k < word.Count; k++) newWord.Add(word[k]);
                    break;
                }
                for (int k = i; k < j; k++) newWord.Add(word[k]);
                if (j < word.Count - 1 && word[j] == first && word[j + 1] == second)
                {
                    newWord.Add(first + second);
                    i = j + 2;
                }
                else
                {
                    newWord.Add(word[j]);
                    i = j + 1;
                }
            }
            word = newWord;
            if (word.Count == 1) break;
            pairs = GetPairs(word);
        }

        var joined = string.Join(" ", word);
        _cache[token] = joined;
        return joined;
    }

    /// <summary>Encode <paramref name="text"/> into BPE token ids (no BOS/EOS/padding).</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        text = WhitespaceClean.Replace(text.Trim(), " ").ToLowerInvariant();
        foreach (Match m in Pat.Matches(text))
        {
            var raw = m.Value;
            var bytes = Encoding.UTF8.GetBytes(raw);
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes) sb.Append(_byteEncoder[b]);
            var byteStr = sb.ToString();
            foreach (var sub in Bpe(byteStr).Split(' '))
            {
                if (_encoder.TryGetValue(sub, out var id))
                    ids.Add(id);
                // (Unknown subwords are silently skipped — CLIP's vocab covers all byte-level BPE outputs.)
            }
        }
        return ids;
    }

    /// <summary>Tokenize and pad to <see cref="MaxLen"/>; returns input_ids and attention_mask as int arrays.</summary>
    public (int[] inputIds, int[] attentionMask) Tokenize(string text)
    {
        var ids = new int[MaxLen];
        var mask = new int[MaxLen];

        var bpeIds = Encode(text);
        // Leave room for BOS + EOS, then truncate.
        if (bpeIds.Count > MaxLen - 2) bpeIds = bpeIds.GetRange(0, MaxLen - 2);

        ids[0] = BosToken;
        mask[0] = 1;
        for (int i = 0; i < bpeIds.Count; i++)
        {
            ids[i + 1] = bpeIds[i];
            mask[i + 1] = 1;
        }
        ids[bpeIds.Count + 1] = EosToken;
        mask[bpeIds.Count + 1] = 1;
        // Remaining positions stay 0 / 0 (pad).
        return (ids, mask);
    }
}

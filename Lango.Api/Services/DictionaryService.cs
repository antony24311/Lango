using System.Text.Json;
using Lango.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Services;

public class DictionaryService
{
    private readonly IHttpClientFactory _hf;
    private readonly AppDb _db;

    public DictionaryService(IHttpClientFactory hf, AppDb db)
    {
        _hf = hf; _db = db;
    }

    // 封裝後的字詞資料
    public async Task<EnrichedWord> FetchAsync(string lemma, CancellationToken ct = default)
    {
        lemma = lemma.Trim().ToLowerInvariant();

        var defs = await FetchDefinitionAsync(lemma, ct);          // dictionaryapi.dev
        var (syn, ant, trg) = await FetchDatamuseAsync(lemma, ct); // Datamuse

        return new EnrichedWord
        {
            Lemma = lemma,
            DefsJson = defs,
            PhoneticsJson = ExtractPhoneticsJson(defs),
            Synonyms = syn.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Antonyms = ant.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Collocations = trg.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    // 寫入/補齊資料到資料庫
    public async Task UpsertAsync(EnrichedWord ew, CancellationToken ct = default)
    {
        var w = await _db.Words.FirstOrDefaultAsync(x => x.Lemma == ew.Lemma, ct);
        if (w is null)
        {
            w = new Word
            {
                Lemma = ew.Lemma,
                DefsJson = ew.DefsJson,
                PhoneticsJson = ew.PhoneticsJson,
                Synonyms = ew.Synonyms,
                Antonyms = ew.Antonyms,
                Collocations = ew.Collocations
            };
            _db.Words.Add(w);
        }
        else
        {
            w.DefsJson ??= ew.DefsJson;
            w.PhoneticsJson ??= ew.PhoneticsJson;
            if (w.Synonyms == null || w.Synonyms.Length == 0) w.Synonyms = ew.Synonyms;
            if (w.Antonyms == null || w.Antonyms.Length == 0) w.Antonyms = ew.Antonyms;
            if (w.Collocations == null || w.Collocations.Length == 0) w.Collocations = ew.Collocations;
        }
        await _db.SaveChangesAsync(ct);
    }

    // ================= 外部 API =================

    // dictionaryapi.dev: https://api.dictionaryapi.dev/api/v2/entries/en/{word}
    private async Task<string?> FetchDefinitionAsync(string lemma, CancellationToken ct)
    {
        var http = _hf.CreateClient();
        var url = $"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(lemma)}";
        try
        {
            var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct); // 原樣存 JSON
        }
        catch
        {
            return null;
        }
    }

    // Datamuse: 同/反/關聯詞
    private async Task<(List<string> syn, List<string> ant, List<string> trg)> FetchDatamuseAsync(string lemma, CancellationToken ct)
    {
        var http = _hf.CreateClient();

        async Task<List<string>> Get(string rel)
        {
            var url = $"https://api.datamuse.com/words?{rel}={Uri.EscapeDataString(lemma)}&max=20";
            try
            {
                using var s = await http.GetStreamAsync(url, ct);
                var arr = await JsonSerializer.DeserializeAsync<List<DatamuseItem>>(s, cancellationToken: ct) ?? new();
                return arr.Select(x => x.word).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        var syn = await Get("rel_syn");
        var ant = await Get("rel_ant");
        var trg = await Get("rel_trg");
        return (syn, ant, trg);
    }

    // 從 dictionaryapi.dev 的 JSON 中擷取 phonetics 欄
    private static string? ExtractPhoneticsJson(string? defsJson)
    {
        if (string.IsNullOrWhiteSpace(defsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(defsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var first = root[0];
            if (first.TryGetProperty("phonetics", out var phon))
                return phon.GetRawText();
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Datamuse 回傳結構
    private record DatamuseItem(string word, int? score);
}

// 對外使用的封裝資料
public class EnrichedWord
{
    public string Lemma { get; set; } = "";
    public string? DefsJson { get; set; }
    public string? PhoneticsJson { get; set; }
    public string[]? Synonyms { get; set; }
    public string[]? Antonyms { get; set; }
    public string[]? Collocations { get; set; }
}

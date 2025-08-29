using System.Text.Json;
using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WordsController : ControllerBase
{
    private readonly AppDb _db;
    public WordsController(AppDb db) { _db = db; }

    /// <summary>
    /// 整理後的單字資訊（若 DB 沒有 defs/phonetics，也會盡量從已存 JSON 解析）
    /// </summary>
    [HttpGet("{lemma}")]
    public async Task<IActionResult> Get(string lemma, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lemma)) return BadRequest("lemma is required.");
        var key = lemma.Trim().ToLowerInvariant();

        var w = await _db.Words.AsNoTracking().FirstOrDefaultAsync(x => x.Lemma == key, ct);
        if (w is null) return NotFound();

        // 解析 dictionaryapi.dev 的 JSON（你之前存的 DefsJson / PhoneticsJson）
        var defs = new List<object>();
        var examples = new List<string>();
        string? phoneticText = null;
        string? audioUrl = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(w.DefsJson))
            {
                using var doc = JsonDocument.Parse(w.DefsJson);
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    // 音標
                    if (phoneticText is null && entry.TryGetProperty("phonetic", out var ph))
                        phoneticText = ph.GetString();

                    // 解析 meanings
                    if (entry.TryGetProperty("meanings", out var meanings))
                    {
                        foreach (var m in meanings.EnumerateArray())
                        {
                            var partOfSpeech = m.TryGetProperty("partOfSpeech", out var pos) ? pos.GetString() : null;
                            if (m.TryGetProperty("definitions", out var arr))
                            {
                                foreach (var d in arr.EnumerateArray())
                                {
                                    var def = d.TryGetProperty("definition", out var dd) ? dd.GetString() : null;
                                    var ex = d.TryGetProperty("example", out var exv) ? exv.GetString() : null;
                                    if (!string.IsNullOrWhiteSpace(def))
                                        defs.Add(new { partOfSpeech, definition = def });
                                    if (!string.IsNullOrWhiteSpace(ex))
                                        examples.Add(ex!);
                                }
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(phoneticText) && !string.IsNullOrWhiteSpace(w.PhoneticsJson))
            {
                using var phdoc = JsonDocument.Parse(w.PhoneticsJson);
                foreach (var p in phdoc.RootElement.EnumerateArray())
                {
                    if (phoneticText is null && p.TryGetProperty("text", out var t))
                        phoneticText = t.GetString();
                    if (audioUrl is null && p.TryGetProperty("audio", out var a))
                    {
                        var url = a.GetString();
                        if (!string.IsNullOrWhiteSpace(url)) audioUrl = url;
                    }
                }
            }
        }
        catch
        {
            // 解析失敗忽略，使用者仍可獲得同/反義詞等
        }

        return Ok(new
        {
            lemma = w.Lemma,
            phonetic = phoneticText,
            audio = audioUrl,
            synonyms = w.Synonyms ?? Array.Empty<string>(),
            antonyms = w.Antonyms ?? Array.Empty<string>(),
            collocations = w.Collocations ?? Array.Empty<string>(),
            definitions = defs,      // [{ partOfSpeech, definition }]
            examples = examples      // [ "..." ]
        });
    }

    /// <summary>
    /// 在你庫內搜尋已儲存單字（前綴/包含）
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("q is required.");
        q = q.Trim().ToLowerInvariant();

        var list = await _db.Words.AsNoTracking()
            .Where(w => EF.Functions.ILike(w.Lemma, q + "%") || EF.Functions.ILike(w.Lemma, "%" + q + "%"))
            .OrderBy(w => w.Lemma)
            .Take(Math.Clamp(take, 1, 50))
            .Select(w => new { w.Id, w.Lemma })
            .ToListAsync(ct);

        return Ok(list);
    }
}

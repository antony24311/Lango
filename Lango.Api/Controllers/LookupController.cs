using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Lango.Api.Services;
namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LookupController : ControllerBase
{
    private readonly AppDb _db;
    private readonly DictionaryService _dict;
    public LookupController(AppDb db, DictionaryService dict) 
    {
        _db = db;
        _dict = dict;
    }
    public record LookupReq(string UserId, string Text, string? Source, string? Sentence);

    [HttpPost]
    public async Task<IActionResult> Post(LookupReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Text is required.");

        var lemma = req.Text.Trim().ToLowerInvariant();

        var word = await _db.Words.FirstOrDefaultAsync(w => w.Lemma == lemma, ct);
        if (word is null)
        {
            // �����X�{�G���r��A�ɸ�ƨäJ�w
            var enriched = await _dict.FetchAsync(lemma, ct);
            await _dict.UpsertAsync(enriched, ct);
            word = await _db.Words.FirstAsync(w => w.Lemma == lemma, ct);

            // �زĤ@���Ʋߡ]���ѡ^
            _db.Reviews.Add(new Review
            {
                UserId = req.UserId,
                WordId = word.Id,
                DueDate = DateTime.UtcNow.Date.AddDays(1),
                NextIntervalDays = 1
            });
        }
        else
        {
            // �w�s�b������� �� ���ոɻ�
            if (word.DefsJson == null || word.Synonyms == null || word.Collocations == null)
            {
                var enriched = await _dict.FetchAsync(lemma, ct);
                await _dict.UpsertAsync(enriched, ct);
                word = await _db.Words.FirstAsync(w => w.Lemma == lemma, ct); // ���s���J
            }
        }

        _db.Lookups.Add(new Lookup
        {
            UserId = req.UserId,
            WordId = word.Id,
            Source = req.Source,
            Sentence = req.Sentence
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            word.Id,
            word.Lemma,
            word.Synonyms,
            word.Antonyms,
            word.Collocations,
            hasDefs = word.DefsJson != null,
            hasPhonetics = word.PhoneticsJson != null
        });
    }
}

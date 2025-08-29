using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GrammarController : ControllerBase
{
    private readonly AppDb _db;
    public GrammarController(AppDb db) { _db = db; }

    public record CheckReq(string UserId, string Sentence);

    [HttpPost("check")]
    public async Task<IActionResult> Check(CheckReq r)
    {
        if (string.IsNullOrWhiteSpace(r.Sentence))
            return BadRequest("Sentence is required.");

        // DEMO：簡化修正；實務請接文法檢查器
        var corrected = r.Sentence
            .Replace("is exist", "exists", StringComparison.OrdinalIgnoreCase)
            .Replace("in Friday", "on Friday", StringComparison.OrdinalIgnoreCase);

        string explainer = "Examples: Use 'exists' (not 'is exist'); preposition 'on Friday'.";
        var s = new Sentence { UserId = r.UserId, Raw = r.Sentence, Corrected = corrected, Explainer = explainer };
        _db.Sentences.Add(s);
        await _db.SaveChangesAsync();

        return Ok(new { corrected, explainer });
    }
}

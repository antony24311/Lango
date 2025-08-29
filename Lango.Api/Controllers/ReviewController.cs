using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReviewController : ControllerBase
{
    private readonly AppDb _db;
    private readonly ReviewScheduler _sched;
    public ReviewController(AppDb db, ReviewScheduler s) { _db = db; _sched = s; }

    public record GradeReq(long WordId, string UserId, int Grade); // 0..5

    [HttpPost("grade")]
    public async Task<IActionResult> Grade(GradeReq r)
    {
        var rv = await _db.Reviews.FirstOrDefaultAsync(x => x.UserId == r.UserId && x.WordId == r.WordId);
        if (rv is null) return NotFound("Review not found");

        var (nextInterval, due) = _sched.Next(r.Grade, rv.NextIntervalDays == 0 ? 1 : rv.NextIntervalDays);
        rv.LastGrade = r.Grade; rv.NextIntervalDays = nextInterval; rv.DueDate = due;

        await _db.SaveChangesAsync();
        return Ok(new { rv.DueDate, rv.NextIntervalDays });
    }

    [HttpGet("today")]
    public async Task<IActionResult> Today([FromQuery] string userId)
    {
        var today = DateTime.UtcNow.Date;
        var list = await _db.Reviews
            .Where(r => r.UserId == userId && r.DueDate <= today)
            .Join(_db.Words, r => r.WordId, w => w.Id, (r, w) => new { w.Id, w.Lemma, r.DueDate })
            .OrderBy(x => x.DueDate).Take(20)
            .ToListAsync();

        return Ok(list);
    }
}

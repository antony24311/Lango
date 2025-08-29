using System.Text.Json;
using System.Text.Json.Serialization;
using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QuizController : ControllerBase
{
    private readonly AppDb _db;
    private readonly ReviewScheduler _sched;

    public QuizController(AppDb db, ReviewScheduler sched)
    {
        _db = db;
        _sched = sched;
    }

    // ===== DTO / Models (只給這個控制器使用) =====
    public record GetTodayReq([property: FromQuery] string userId, [property: FromQuery] int count = 12);

    public record CreateOrFetchResp(long quizId, DateTime forDate, object payload);

    public record SubmitReq(string UserId, long QuizId, List<AnswerItem> Answers);
    public record AnswerItem(string QuestionId, string? Answer);

    // 送回前端的題目格式
    public class QuizPayload
    {
        [JsonPropertyName("items")]
        public List<QuizItem> Items { get; set; } = new();
    }

    public class QuizItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Type { get; set; } = "spell"; // spell | choice
        public long WordId { get; set; }
        public string Prompt { get; set; } = "";    // 題目敘述
        public string? Hint { get; set; }           // 提示
        public List<string>? Options { get; set; }  // 選擇題選項（若有）
        public string Answer { get; set; } = "";    // 正解（用來批改）
    }

    // ===== 1) 取得/建立 今日題單 =====
    [HttpGet("today")]
    public async Task<ActionResult<CreateOrFetchResp>> Today([FromQuery] GetTodayReq rq)
    {
        if (string.IsNullOrWhiteSpace(rq.userId))
            return BadRequest("userId is required.");

        var today = DateTime.UtcNow.Date;

        // 已存在的今日題單 → 直接回傳
        var existing = await _db.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.UserId == rq.userId && q.ForDate == today);

        if (existing is not null)
        {
            var payload = JsonSerializer.Deserialize<QuizPayload>(existing.PayloadJson) ?? new QuizPayload();
            return Ok(new CreateOrFetchResp(existing.Id, existing.ForDate, payload));
        }

        // 沒有 → 建立一份
        var payloadNew = await BuildQuizPayload(rq.userId, rq.count);
        var quiz = new Quiz
        {
            UserId = rq.userId,
            ForDate = today,
            PayloadJson = JsonSerializer.Serialize(payloadNew),
            Done = false
        };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();

        return Ok(new CreateOrFetchResp(quiz.Id, quiz.ForDate, payloadNew));
    }

    // ===== 2) 交卷（批改 + 更新 Review） =====
    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitReq req)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == req.QuizId && q.UserId == req.UserId);
        if (quiz is null) return NotFound("Quiz not found.");
        if (quiz.Done) return BadRequest("Quiz already submitted.");

        var payload = JsonSerializer.Deserialize<QuizPayload>(quiz.PayloadJson) ?? new QuizPayload();

        // 建立答案對照
        var key = payload.Items.ToDictionary(x => x.Id, x => x);

        int correct = 0;
        var details = new List<object>();

        foreach (var a in req.Answers)
        {
            if (!key.TryGetValue(a.QuestionId, out var item))
                continue;

            var userAns = (a.Answer ?? "").Trim();
            var gold = (item.Answer ?? "").Trim();

            bool isCorrect;
            if (item.Type == "spell")
            {
                isCorrect = string.Equals(userAns, gold, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isCorrect = string.Equals(userAns, gold, StringComparison.OrdinalIgnoreCase);
            }

            if (isCorrect) correct++;

            // 批改結果詳情
            details.Add(new
            {
                questionId = item.Id,
                type = item.Type,
                wordId = item.WordId,
                prompt = item.Prompt,
                yourAnswer = userAns,
                correctAnswer = gold,
                correct = isCorrect
            });

            // ===== 用答題結果更新 Review（正確→給 4 分；錯誤→給 2 分） =====
            var rv = await _db.Reviews.FirstOrDefaultAsync(r => r.UserId == req.UserId && r.WordId == item.WordId);
            if (rv != null)
            {
                var grade = isCorrect ? 4 : 2;
                var (nextInterval, due) = _sched.Next(grade, rv.NextIntervalDays == 0 ? 1 : rv.NextIntervalDays);
                rv.LastGrade = grade;
                rv.NextIntervalDays = nextInterval;
                rv.DueDate = due;
            }
        }

        quiz.Done = true;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            quizId = quiz.Id,
            total = payload.Items.Count,
            correct,
            score = Math.Round(100.0 * correct / Math.Max(1, payload.Items.Count), 1),
            details
        });
    }

    // ===== 建題邏輯：優先抽今日到期的 Review，再用最近查過的字補足 =====
    private async Task<QuizPayload> BuildQuizPayload(string userId, int count)
    {
        var today = DateTime.UtcNow.Date;

        // 1) 今日到期（優先）
        var due = await _db.Reviews
            .Where(r => r.UserId == userId && r.DueDate <= today)
            .Join(_db.Words, r => r.WordId, w => w.Id, (r, w) => new { w.Id, w.Lemma, w.Synonyms, w.Antonyms, w.Collocations })
            .OrderBy(r => r.Id)
            .Take(count)
            .ToListAsync();

        var selectedIds = due.Select(x => x.Id).ToHashSet();

        // 2) 若不足，補上最近查過但未選入的字
        if (due.Count < count)
        {
            var remain = count - due.Count;
            var recent = await _db.Lookups
                .Where(l => l.UserId == userId && !selectedIds.Contains(l.WordId))
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => l.WordId)
                .Distinct()
                .Take(remain)
                .Join(_db.Words, wid => wid, w => w.Id,
                    (wid, w) => new { w.Id, w.Lemma, w.Synonyms, w.Antonyms, w.Collocations })
                .ToListAsync();
            due.AddRange(recent);
        }

        // 3) 轉成題目（盡量多型態；若無同/反義詞資料，就做拼字題）
        var rng = new Random();
        var items = new List<QuizItem>();

        foreach (var w in due)
        {
            // 3a) 拼字（主題型，人人有）
            items.Add(BuildSpellItem(w));

            // 3b) 如果有同義詞資料，出一題單選
            var syns = w.Synonyms ?? Array.Empty<string>();
            if (syns.Length >= 1 && items.Count < count)
            {
                items.Add(BuildChoiceItem(w, "Which word is a synonym of the target word?", syns, rng));
            }

            if (items.Count >= count) break;
        }

        // 如果還是不夠，全部用拼字題補滿
        while (items.Count < count && due.Count > 0)
        {
            var w = due[items.Count % due.Count];
            items.Add(BuildSpellItem(w));
        }

        return new QuizPayload { Items = items };
    }

    private static QuizItem BuildSpellItem(dynamic w)
    {
        var lemma = (string)w.Lemma;
        var masked = MaskWord(lemma);
        return new QuizItem
        {
            Type = "spell",
            WordId = (long)w.Id,
            Prompt = $"Fill in the missing letters: {masked}",
            Hint = $"Length {lemma.Length}, starts with '{lemma[0]}'",
            Answer = lemma
        };
    }

    private static QuizItem BuildChoiceItem(dynamic w, string prompt, string[] pool, Random rng)
    {
        var lemma = (string)w.Lemma;
        // 正解 = pool 之一；干擾選項 = 從 pool 其他字或 lemma 衍生字組（簡化）
        var correct = pool[rng.Next(pool.Length)];
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { correct };
        while (options.Count < 4)
        {
            var candidate = pool[rng.Next(pool.Length)];
            if (!options.Contains(candidate))
                options.Add(candidate);
        }
        var shuffled = options.OrderBy(_ => rng.Next()).ToList();

        return new QuizItem
        {
            Type = "choice",
            WordId = (long)w.Id,
            Prompt = $"{prompt}\n(Target: {lemma})",
            Options = shuffled,
            Answer = correct
        };
    }

    // 將單字中間若干字母遮住（保留首尾）
    private static string MaskWord(string w)
    {
        if (w.Length <= 2) return w[0] + "_";
        var middle = new string('_', Math.Max(1, w.Length - 2));
        return $"{w[0]}{middle}{w[^1]}";
    }
}

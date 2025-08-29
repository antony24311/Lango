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

    // ===== DTO / Models (�u���o�ӱ���ϥ�) =====
    public record GetTodayReq([property: FromQuery] string userId, [property: FromQuery] int count = 12);

    public record CreateOrFetchResp(long quizId, DateTime forDate, object payload);

    public record SubmitReq(string UserId, long QuizId, List<AnswerItem> Answers);
    public record AnswerItem(string QuestionId, string? Answer);

    // �e�^�e�ݪ��D�خ榡
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
        public string Prompt { get; set; } = "";    // �D�رԭz
        public string? Hint { get; set; }           // ����
        public List<string>? Options { get; set; }  // ����D�ﶵ�]�Y���^
        public string Answer { get; set; } = "";    // ���ѡ]�Ψӧ��^
    }

    // ===== 1) ���o/�إ� �����D�� =====
    [HttpGet("today")]
    public async Task<ActionResult<CreateOrFetchResp>> Today([FromQuery] GetTodayReq rq)
    {
        if (string.IsNullOrWhiteSpace(rq.userId))
            return BadRequest("userId is required.");

        var today = DateTime.UtcNow.Date;

        // �w�s�b�������D�� �� �����^��
        var existing = await _db.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.UserId == rq.userId && q.ForDate == today);

        if (existing is not null)
        {
            var payload = JsonSerializer.Deserialize<QuizPayload>(existing.PayloadJson) ?? new QuizPayload();
            return Ok(new CreateOrFetchResp(existing.Id, existing.ForDate, payload));
        }

        // �S�� �� �إߤ@��
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

    // ===== 2) ����]��� + ��s Review�^ =====
    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitReq req)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == req.QuizId && q.UserId == req.UserId);
        if (quiz is null) return NotFound("Quiz not found.");
        if (quiz.Done) return BadRequest("Quiz already submitted.");

        var payload = JsonSerializer.Deserialize<QuizPayload>(quiz.PayloadJson) ?? new QuizPayload();

        // �إߵ��׹��
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

            // ��ﵲ�G�Ա�
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

            // ===== �ε��D���G��s Review�]���T���� 4 ���F���~���� 2 ���^ =====
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

    // ===== ���D�޿�G�u���⤵������ Review�A�A�γ̪�d�L���r�ɨ� =====
    private async Task<QuizPayload> BuildQuizPayload(string userId, int count)
    {
        var today = DateTime.UtcNow.Date;

        // 1) �������]�u���^
        var due = await _db.Reviews
            .Where(r => r.UserId == userId && r.DueDate <= today)
            .Join(_db.Words, r => r.WordId, w => w.Id, (r, w) => new { w.Id, w.Lemma, w.Synonyms, w.Antonyms, w.Collocations })
            .OrderBy(r => r.Id)
            .Take(count)
            .ToListAsync();

        var selectedIds = due.Select(x => x.Id).ToHashSet();

        // 2) �Y�����A�ɤW�̪�d�L������J���r
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

        // 3) �ন�D�ء]�ɶq�h���A�F�Y�L�P/�ϸq����ơA�N�����r�D�^
        var rng = new Random();
        var items = new List<QuizItem>();

        foreach (var w in due)
        {
            // 3a) ���r�]�D�D���A�H�H���^
            items.Add(BuildSpellItem(w));

            // 3b) �p�G���P�q����ơA�X�@�D���
            var syns = w.Synonyms ?? Array.Empty<string>();
            if (syns.Length >= 1 && items.Count < count)
            {
                items.Add(BuildChoiceItem(w, "Which word is a synonym of the target word?", syns, rng));
            }

            if (items.Count >= count) break;
        }

        // �p�G�٬O�����A�����Ϋ��r�D�ɺ�
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
        // ���� = pool ���@�F�z�Z�ﶵ = �q pool ��L�r�� lemma �l�ͦr�ա]²�ơ^
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

    // �N��r�����Y�z�r���B��]�O�d�����^
    private static string MaskWord(string w)
    {
        if (w.Length <= 2) return w[0] + "_";
        var middle = new string('_', Math.Max(1, w.Length - 2));
        return $"{w[0]}{middle}{w[^1]}";
    }
}

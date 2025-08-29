using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lango.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExportController : ControllerBase
{
    private readonly AppDb _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

    public ExportController(AppDb db, IHttpClientFactory http, IConfiguration cfg)
    {
        _db = db; _http = http; _cfg = cfg;
    }

    /// <summary>
    /// 產生今日題單的 iCalendar 檔（.ics）。下載後可匯入 iOS/Google Calendar。
    /// </summary>
    [HttpGet("ics")]
    public async Task<IActionResult> GetIcs([FromQuery] string userId, [FromQuery] string? tz = null, [FromQuery] string? time = "20:00")
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("userId is required.");

        var today = DateTime.UtcNow.Date;

        // 取今日題單，不存在則建立（重用 QuizController 的邏輯：此處用最小化版本）
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.UserId == userId && q.ForDate == today);
        if (quiz is null)
        {
            // 若沒有題單，建一份只有單字列表（最簡化：取今日到期的複習）
            var due = await _db.Reviews
                .Where(r => r.UserId == userId && r.DueDate <= today)
                .Join(_db.Words, r => r.WordId, w => w.Id, (r, w) => w.Lemma)
                .Take(20).ToListAsync();

            var payload = new { items = due.Select((x, i) => new { id = i, type = "spell", prompt = x, answer = x }) };
            quiz = new Quiz
            {
                UserId = userId,
                ForDate = today,
                PayloadJson = JsonSerializer.Serialize(payload),
                Done = false
            };
            _db.Quizzes.Add(quiz);
            await _db.SaveChangesAsync();
        }

        var payloadJson = quiz.PayloadJson;
        var words = ExtractWordsFromQuizJson(payloadJson);

        // 時區/時間處理：預設 Asia/Taipei，每晚 20:00
        var tzName = tz ?? _cfg["Export:DefaultTimeZone"] ?? "Asia/Taipei";
        var hhmm = (time ?? "20:00").Split(':');
        var startLocal = new DateTime(today.Year, today.Month, today.Day, int.Parse(hhmm[0]), int.Parse(hhmm[1]), 0, DateTimeKind.Unspecified);

        // 生成 ICS 內容（簡單單事件）
        var uid = $"{Guid.NewGuid()}@lango";
        var summary = "Lango：今日英文練習";
        var desc = string.Join("\\n", words.Select((w, i) => $"{i + 1}. {w}"));
        var ics = BuildIcs(uid, startLocal, durationMinutes: 20, summary, desc, tzName);

        var bytes = Encoding.UTF8.GetBytes(ics);
        return File(bytes, "text/calendar", $"Lango-{today:yyyyMMdd}.ics");
    }

    /// <summary>
    /// 匯出今日題單到 Notion Database（需要環境變數 NOTION_TOKEN 與 NOTION_DATABASE_ID）
    /// </summary>
    [HttpPost("notion")]
    public async Task<IActionResult> ExportNotion([FromQuery] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("userId is required.");

        var token = Environment.GetEnvironmentVariable("NOTION_TOKEN") ?? _cfg["Notion:Token"];
        var databaseId = Environment.GetEnvironmentVariable("NOTION_DATABASE_ID") ?? _cfg["Notion:DatabaseId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(databaseId))
            return BadRequest("NOTION_TOKEN / NOTION_DATABASE_ID not configured.");

        var today = DateTime.UtcNow.Date;
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.UserId == userId && q.ForDate == today);
        if (quiz is null)
            return NotFound("No quiz for today. Call /api/quiz/today first.");

        var words = ExtractWordsFromQuizJson(quiz.PayloadJson);

        var client = _http.CreateClient();
        client.BaseAddress = new Uri("https://api.notion.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // 建立頁面 payload：標題=日期，內容=bulleted list（詞彙清單）
        var body = new
        {
            parent = new { database_id = databaseId },
            properties = new
            {
                Name = new
                {
                    title = new[]
                    {
                        new { type = "text", text = new { content = $"Lango 今日練習 {today:yyyy-MM-dd}" } }
                    }
                }
            },
            children = words.Select(w => new
            {
                @object = "block",
                type = "bulleted_list_item",
                bulleted_list_item = new
                {
                    rich_text = new[] { new { type = "text", text = new { content = w } } }
                }
            }).ToArray()
        };

        var resp = await client.PostAsync("v1/pages",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return StatusCode((int)resp.StatusCode, raw);

        // 回傳 Notion 頁面簡要資訊
        return Ok(new { ok = true, today = today, wordsCount = words.Count, notionResponse = JsonDocument.Parse(raw).RootElement });
    }

    // ===== Helper：從 Quiz 的 JSON 抽出「題單中的單字」清單（簡化：看 prompt/answer）
    private static List<string> ExtractWordsFromQuizJson(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var items = root.TryGetProperty("items", out var arr) ? arr : root.GetProperty("Items"); // 兼容大小寫
            var list = new List<string>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.String)
                    list.Add(ans.GetString()!);
                else if (it.TryGetProperty("answer", out var ans2) && ans2.ValueKind == JsonValueKind.String)
                    list.Add(ans2.GetString()!);
                else if (it.TryGetProperty("Prompt", out var p) && p.ValueKind == JsonValueKind.String)
                    list.Add(p.GetString()!);
                else if (it.TryGetProperty("prompt", out var p2) && p2.ValueKind == JsonValueKind.String)
                    list.Add(p2.GetString()!);
            }
            // 去重、保留順序
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ===== Helper：組 ICS 文字（單事件）
    private static string BuildIcs(string uid, DateTime localStart, int durationMinutes, string summary, string description, string tzName)
    {
        // 將 localStart 當作浮動時間（交給行事曆依 tzName 詮釋），簡化處理
        var dtStart = localStart.ToString("yyyyMMdd'T'HHmmss");
        var dtEnd = localStart.AddMinutes(durationMinutes).ToString("yyyyMMdd'T'HHmmss");
        var nowStamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//Lango//EN");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("METHOD:PUBLISH");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{uid}");
        sb.AppendLine($"DTSTAMP:{nowStamp}");
        sb.AppendLine($"DTSTART;TZID={tzName}:{dtStart}");
        sb.AppendLine($"DTEND;TZID={tzName}:{dtEnd}");
        sb.AppendLine($"SUMMARY:{EscapeIcs(summary)}");
        sb.AppendLine($"DESCRIPTION:{EscapeIcs(description)}");
        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string EscapeIcs(string s) =>
        s.Replace(@"\", @"\\").Replace(";", @"\;").Replace(",", @"\,").Replace("\n", "\\n");
}

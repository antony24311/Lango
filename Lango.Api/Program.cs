// Program.cs
using Lango.Api.Data;
using Microsoft.EntityFrameworkCore;
using Lango.Api.Services;
var builder = WebApplication.CreateBuilder(args);

// ? 必須註冊：Controller / API 探索 / Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddScoped<DictionaryService>();
// ? 註冊 DbContext（連線字串 key: "Db"）
builder.Services.AddDbContext<AppDb>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Db")));

builder.Services.AddScoped<ReviewScheduler>();
builder.Services.AddHttpClient();
var app = builder.Build();

// （可選）開發環境啟用 Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();

// ? 這行需要前面已經 AddControllers()
app.MapControllers();

app.Run();


app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

/// <summary>簡化版間隔複習演算法（SM-2 風格）</summary>
public class ReviewScheduler
{
    public (int nextIntervalDays, DateTime dueDate) Next(int? lastGrade, int prevIntervalDays)
    {
        if (lastGrade is null || lastGrade < 3) return (1, DateTime.UtcNow.Date.AddDays(1));
        var mult = lastGrade switch { 3 => 1.7, 4 => 2.2, 5 => 2.8, _ => 1.5 };
        var next = Math.Max(2, (int)Math.Round(Math.Max(1, prevIntervalDays) * mult));
        return (next, DateTime.UtcNow.Date.AddDays(next));
    }
}

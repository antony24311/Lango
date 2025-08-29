// Program.cs
using Lango.Api.Data;
using Microsoft.EntityFrameworkCore;
using Lango.Api.Services;
var builder = WebApplication.CreateBuilder(args);

// ? �������U�GController / API ���� / Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddScoped<DictionaryService>();
// ? ���U DbContext�]�s�u�r�� key: "Db"�^
builder.Services.AddDbContext<AppDb>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Db")));

builder.Services.AddScoped<ReviewScheduler>();
builder.Services.AddHttpClient();
var app = builder.Build();

// �]�i��^�}�o���ұҥ� Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();

// ? �o��ݭn�e���w�g AddControllers()
app.MapControllers();

app.Run();


app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

/// <summary>²�ƪ����j�Ʋߺt��k�]SM-2 ����^</summary>
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

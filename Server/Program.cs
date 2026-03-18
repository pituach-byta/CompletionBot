using CompletionBot.Server.Services;
using Microsoft.Extensions.FileProviders; // חובה עבור BotUploads

var builder = WebApplication.CreateBuilder(args);

// --- 1. הוספת שירותים (Services) ---
builder.Services.AddControllers(); // חובה ל-API
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// רישום שירות הדאטה-בייס
builder.Services.AddScoped<DbService>();

// הגדרת CORS - קריטי לתקשורת עם הדפדפן
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy
            .WithOrigins(
                "https://auto-office.byta.org.il",
                "http://localhost:5173",
                "https://localhost:5173"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

var app = builder.Build();

// --- 2. הגדרת ה-Pipeline (הסדר כאן קריטי!) ---

// א. Swagger
app.UseSwagger();
app.UseSwaggerUI();

// ב. CORS
app.UseCors("AllowAll");

// ג. הגדרת ניתוב בסיסית
app.UseRouting();

// ד. הגדרת תיקיית ההעלאות המיוחדת (BotUploads) - החלק ששמרנו
var uploadsPath = Path.Combine(builder.Environment.ContentRootPath, "BotUploads");
if (!Directory.Exists(uploadsPath))
{
    Directory.CreateDirectory(uploadsPath);
}

// חשיפת קבצי BotUploads
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/BotUploads",
    OnPrepareResponse = ctx =>
    {
        // מגדיר הורדה אוטומטית לקבצים אלו
        ctx.Context.Response.Headers.Append(
            "Content-Disposition", $"attachment; filename={ctx.File.Name}");
    }
});

// ה. הגדרת קבצים סטטיים רגילים (עבור ה-React/אתר)
app.UseDefaultFiles();
app.UseStaticFiles();

// ו. אימות
app.UseAuthorization();

// ז. המיפויים בפועל - התיקון הקריטי לתשלום!
// 1. קודם כל בודקים אם יש Controller מתאים (כמו PaymentCallback)
app.MapControllers();

// 2. רק אם לא נמצא Controller, שולחים את דף ה-React (מונע מסך לבן ב-API)
app.MapFallbackToFile("index.html");

app.Run();
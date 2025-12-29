using CompletionBot.Server.Services;
using Microsoft.Extensions.FileProviders; // הוספתי את זה - חובה כדי לזהות קבצים פיזיים

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// הגדרות Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// רישום שירות הדאטה-בייס (DbService)
builder.Services.AddScoped<DbService>();

// הגדרת CORS גמישה שתעבוד עם כל פורט
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", 
        policy => policy
            .AllowAnyOrigin()   
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

// הגדרת Swagger שתמיד יעבוד בסביבת פיתוח
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// הפעלת ה-CORS עם המדיניות החדשה
app.UseCors("AllowAll");

// ביטול ההפניה הכפויה ל-HTTPS (נשאר בהערה כפי שביקשת)
// app.UseHttpsRedirection(); 

// --- התחלת הקוד החדש: פתיחת הגישה לקבצים ---
// 1. הגדרת הנתיב הפיזי לתיקייה
var uploadsPath = Path.Combine(builder.Environment.ContentRootPath, "BotUploads");

// 2. ווידוא שהתיקייה קיימת (למניעת שגיאות אם מחקת אותה)
if (!Directory.Exists(uploadsPath))
{
    Directory.CreateDirectory(uploadsPath);
}

// 3. הגדרת השרת לאפשר גישה לקבצים בנתיב זה דרך הדפדפן
// --- החליפי את החלק של app.UseStaticFiles בקוד הזה: ---

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/BotUploads",
    // הפונקציה הזו רצה בכל פעם שמישהו מבקש קובץ
    OnPrepareResponse = ctx =>
    {
        // מגדיר לדפדפן שהקובץ הוא "קובץ מצורף" (Attachment) שחובה להוריד
        // וגם נותן לו את השם המקורי
        ctx.Context.Response.Headers.Append(
            "Content-Disposition", $"attachment; filename={ctx.File.Name}");
    }
});
// --- סוף הקוד החדש ---

app.UseAuthorization();

app.MapControllers();

app.Run();
using CompletionBot.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// הגדרות Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// רישום שירות הדאטה-בייס (DbService)
builder.Services.AddScoped<DbService>();

// הגדרת CORS (כדי לאפשר ל-React בפורט 5173 להתחבר לשרת בפורט 5219)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins("http://localhost:5173") // הכתובת של הלקוח (React)
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

// הפעלת ה-CORS
app.UseCors("AllowReactApp");

// >>> התיקון הקריטי: ביטול ההפניה הכפויה ל-HTTPS <<<
// app.UseHttpsRedirection(); 

app.UseAuthorization();

app.MapControllers();

app.Run();
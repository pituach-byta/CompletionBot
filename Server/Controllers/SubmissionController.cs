using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using CompletionBot.Server.Models;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubmissionController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly IWebHostEnvironment _env; // שימוש בסביבת הריצה לנתיבים מדויקים

        public SubmissionController(DbService dbService, IWebHostEnvironment env)
        {
            _dbService = dbService;
            _env = env;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile([FromForm] int debtId, [FromForm] IFormFile file)
        {
            // 1. בדיקות תקינות בסיסיות לקובץ
            if (file == null || file.Length == 0)
                return BadRequest("לא נבחר קובץ.");

            // בדיקת סיומת קובץ - תומך במסמכים ותמונות
            var ext = Path.GetExtension(file.FileName).ToLower();
            var allowedExtensions = new[] { ".pdf", ".docx", ".doc", ".jpg", ".png", ".jpeg" };
            
            if (!allowedExtensions.Contains(ext))
                return BadRequest($"סוג קובץ לא נתמך ({ext}). יש להעלות PDF או Word.");

            try
            {
                // 2. יצירת נתיב שמירה בטוח (Absolute Path)
                // התיקייה תיווצר בתוך התיקייה של הפרויקט: Server/BotUploads
                var uploadFolder = Path.Combine(_env.ContentRootPath, "BotUploads");
                
                if (!Directory.Exists(uploadFolder))
                {
                    Console.WriteLine($"Creating folder: {uploadFolder}");
                    Directory.CreateDirectory(uploadFolder);
                }

                // 3. יצירת שם קובץ ייחודי (מונע דריסת קבצים קודמים אם מעלים שוב)
                var uniqueFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0,8)}{ext}";
                var fullPath = Path.Combine(uploadFolder, uniqueFileName);

                Console.WriteLine($"Saving submission for DebtID {debtId} to: {fullPath}");

                // 4. שמירת הקובץ פיזית בדיסק
                using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // 5. עדכון סטטוס "הוגש" (IsSubmitted) בנפרד מסטטוס "שולם" (IsPaid)
                // הפעולה הזו לא נוגעת בסטטוס התשלום, כך שהתלמידה יכולה להגיש עבודה
                // גם אם שילמה מזמן, וגם אם היא מעלה תיקון לעבודה קיימת.
                await _dbService.MarkDebtAsSubmittedAsync(debtId);

                return Ok(new { 
                    message = "הקובץ הועלה וההגשה נקלטה בהצלחה", 
                    fileName = uniqueFileName,
                    status = "Submitted" // אינדיקציה ללקוח שהסטטוס השתנה
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Upload Error: {ex.Message}");
                return StatusCode(500, $"שגיאה בשמירת הקובץ בשרת: {ex.Message}");
            }
        }
    }
}
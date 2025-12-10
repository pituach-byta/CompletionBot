using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using Dapper;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubmissionController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly IWebHostEnvironment _env;

        public SubmissionController(DbService dbService, IWebHostEnvironment env)
        {
            _dbService = dbService;
            _env = env;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile([FromForm] IFormFile file, [FromForm] int debtId, [FromForm] string studentId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("לא נבחר קובץ");

            try
            {
                // 1. שמירת הקובץ הפיזי בשרת
                var uploadsFolder = Path.Combine(_env.ContentRootPath, "BotUploads");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                // יצירת שם קובץ ייחודי
                var uniqueFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(file.FileName)}";
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // 2. עדכון מסד הנתונים
                using var connection = _dbService.CreateConnection();
                connection.Open();
                using var transaction = connection.BeginTransaction();

                try
                {
                    // א. עדכון הטבלה הרגילה (בשביל הבוט)
                    var updateDebtSql = "UPDATE StudentDebts SET IsSubmitted = 1, LastUpdated = GETDATE() WHERE DebtID = @DebtID";
                    await connection.ExecuteAsync(updateDebtSql, new { DebtID = debtId }, transaction);

                    // ב. הוספת שורה לטבלת ההיסטוריה (בשביל הדוח!!)
                    // זה החלק שהיה חסר או לא התעדכן בגלל הנעילה
                    var insertHistorySql = @"
                        INSERT INTO Submissions (DebtID, StudentID, UploadDate, FilePath, FileName)
                        VALUES (@DebtID, @StudentID, GETDATE(), @FilePath, @FileName)";

                    await connection.ExecuteAsync(insertHistorySql, new { 
                        DebtID = debtId, 
                        StudentID = studentId, 
                        FilePath = uniqueFileName, 
                        FileName = file.FileName 
                    }, transaction);

                    transaction.Commit();
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }

                return Ok("הקובץ הועלה וההגשה תועדה בהצלחה");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בהעלאת הקובץ: " + ex.Message);
            }
        }
    }
}
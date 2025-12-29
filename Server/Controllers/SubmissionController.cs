using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;

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
        public async Task<IActionResult> UploadWork(
            [FromForm] string studentId, 
            [FromForm] int debtId, 
            [FromForm] List<IFormFile> files) 
        {
            if (string.IsNullOrEmpty(studentId) || debtId == 0 || files == null || files.Count == 0)
                return BadRequest("נתונים חסרים");

            try
            {
                var student = await _dbService.GetStudentByIdAsync(studentId);
                if (student == null) return NotFound("תלמידה לא נמצאה");

                var uploadsPath = Path.Combine(_env.ContentRootPath, "BotUploads");
                if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);

                var uploadedLinks = new List<string>();

                foreach (var file in files)
                {
                    if (file.Length > 0)
                    {
                        // יצירת שם קובץ באנגלית בלבד (ת"ז + קוד חוב + מזהה ייחודי)
                        var extension = Path.GetExtension(file.FileName).ToLower();
                        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8); 
                        var safeFileName = $"{studentId}_{debtId}_{uniqueId}{extension}";
                        
                        var filePath = Path.Combine(uploadsPath, safeFileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }

                        // יצירת הקישור המלא
                        string fileLink = $"{Request.Scheme}://{Request.Host}/api/admin/download/{safeFileName}";
                        uploadedLinks.Add(fileLink);
                    }
                }

                // חיבור כל הקישורים למחרוזת אחת
                string finalLinksString = string.Join(" , ", uploadedLinks);
                
                // קריאה לפונקציה המעודכנת ב-DbService (עם StudentID)
                await _dbService.SaveSubmissionAsync(debtId, studentId, finalLinksString);

                return Ok(new { message = "העבודה הוגשה בהצלחה" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return StatusCode(500, $"שגיאה בהעלאה: {ex.Message}");
            }
        }
    }
}
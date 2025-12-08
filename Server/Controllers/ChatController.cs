using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using CompletionBot.Server.Models;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public ChatController(DbService dbService, IConfiguration config)
        {
            _dbService = dbService;
            _apiKey = config["OpenAI:ApiKey"] ?? "";
            _httpClient = new HttpClient();
        }

        [HttpPost("message")]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            try 
            {
                // --- שלב א: כניסה למערכת (הזנת ת"ז) ---
                if (string.IsNullOrEmpty(request.StudentId))
                {
                    var inputId = request.UserMessage.Trim();
                    
                    if (string.IsNullOrWhiteSpace(inputId) || inputId.Length < 8 || !inputId.All(char.IsDigit)) 
                        return Ok(new BotResponse { Reply = "נא להזין תעודת זהות תקינה (ספרות בלבד)." });

                    var student = await _dbService.GetStudentByIdAsync(inputId);
                    if (student == null)
                        return Ok(new BotResponse { Reply = "לא מצאתי תלמידה עם מספר זהות זה במערכת. אנא נסי שנית." });

                    var debts = await _dbService.GetDebtsByStudentIdAsync(inputId);
                    
                    if (!debts.Any())
                    {
                        return Ok(new BotResponse { 
                            Reply = $"שלום {student.FirstName}. שמח לבשר שאין לך חובות פתוחים במערכת.",
                            StudentId = student.StudentID
                        });
                    }

                    // בניית המידע המלא ללקוח
                    var debtsData = debts.Select(d => new {
                        d.DebtID,
                        d.LessonName,
                        d.LessonType,
                        d.LecturerName,
                        d.IsPaid,
                        d.IsSubmitted,
                        d.MaterialLink,
                        d.Hours, // חובה להעביר את השעות לחישוב ה-300
                        StudentID = student.StudentID,
                        FirstName = student.FirstName,
                        LastName = student.LastName
                    });

                    return Ok(new BotResponse 
                    { 
                        Reply = $"שלום {student.FirstName}, זוהית בהצלחה. נמצאו {debts.Count()} חובות במערכת.",
                        StudentId = student.StudentID,
                        ActionType = "ShowDebts",
                        Data = debtsData
                    });
                }

                // --- שלב ב: שיחה רגילה ---
                var currentStudent = await _dbService.GetStudentByIdAsync(request.StudentId);
                if (currentStudent == null) return Unauthorized("שגיאת זיהוי: תלמידה לא נמצאה");

                var currentDebts = await _dbService.GetDebtsByStudentIdAsync(request.StudentId);
                
                var responseData = currentDebts.Select(d => new {
                    d.DebtID, d.LessonName, d.LessonType, d.LecturerName, d.IsPaid, d.IsSubmitted, d.MaterialLink, d.Hours,
                    StudentID = currentStudent.StudentID,
                    FirstName = currentStudent.FirstName,
                    LastName = currentStudent.LastName
                });

                // בודקים אם נשאר משהו לשלם
                var hasUnpaid = currentDebts.Any(d => !d.IsPaid);

                if (hasUnpaid)
                {
                    return Ok(new BotResponse 
                    { 
                        Reply = "יש להסדיר את התשלום עבור כל החובות שטרם שולמו.",
                        StudentId = request.StudentId,
                        ActionType = "ShowDebts",
                        Data = responseData
                    });
                }
                else
                {
                    return Ok(new BotResponse 
                    { 
                        Reply = "התשלום התקבל בהצלחה! כעת ניתן להוריד את החומרים ולהגיש את העבודות (עד מכסת השעות).",
                        StudentId = request.StudentId,
                        ActionType = "UploadFile",
                        Data = responseData
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in ChatController: " + ex.Message);
                return StatusCode(500, new { message = "שגיאה פנימית בשרת", error = ex.Message });
            }
        }
    }
}
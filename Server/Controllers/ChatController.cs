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
                            Reply = $"שלום {student.FirstName}. איזה כיף לבשר לך שאין לך חובות פתוחים במערכת! הכל הושלם בהצלחה.",
                            StudentId = student.StudentID
                        });
                    }

                    // --- כאן מתחיל השדרוג ה"אנושי" ---
                    
                    // 1. מיון החובות לקבוצות
                    var submitted = debts.Where(d => d.IsSubmitted).ToList(); // עבודות שהושלמו
                    var paidNotSubmitted = debts.Where(d => d.IsPaid && !d.IsSubmitted).ToList(); // שולם אך טרם הוגש
                    var unpaid = debts.Where(d => !d.IsPaid).ToList(); // לא שולם

                    // 2. בניית הודעה אישית בגוף נקבה
                    var sb = new StringBuilder();
                    sb.Append($"שלום לך {student.FirstName}, אני שמחה שחזרת אלי. ");

                    // תרחיש: יש דברים שכבר הסתיימו
                    if (submitted.Any())
                    {
                        // לוקחים רק את ה-2 האחרונות לדוגמה כדי לא להעמיס
                        var courseNames = string.Join(", ", submitted.Take(2).Select(d => d.LessonName));
                        var moreText = submitted.Count > 2 ? " ועוד..." : "";
                        sb.Append($"ראיתי שכבר השלמת בהצלחה את העבודות בקורסים: {courseNames}{moreText}. כל הכבוד! ");
                    }

                    // תרחיש: שילמה אבל לא הגישה (הכי חשוב להזכיר לה)
                    if (paidNotSubmitted.Any())
                    {
                        sb.Append("\n\n"); // ירידת שורה
                        sb.Append($"בפעם האחרונה ביצעת תשלום עבור {paidNotSubmitted.Count} קורסים, וכעת נותר לך רק להעלות את קבצי העבודות.");
                    }
                    // תרחיש: לא שילמה
                    else if (unpaid.Any())
                    {
                        sb.Append("\n\n");
                        sb.Append($"נותרו לך {unpaid.Count} חובות שטרם הוסדרו. יש לבצע תשלום כדי לקבל את חומרי הלמידה.");
                    }

                    sb.Append("\nהנה רשימת המטלות שנשארו לך לטיפול:");

                    // 3. סינון הנתונים ללקוח
                    // נציג בטבלה קודם את מה שדחוף (לא הוגש), ואת מה שכבר הוגש נשים בסוף או נסנן אם תרצי
                    var sortedDebts = debts
                    .Where(d => !d.IsSubmitted)  // <--- מסנן החוצה את מה שכבר הוגש
                    .OrderBy(d => d.IsPaid)      // ממיין: קודם מה שלא שולם
                    .ToList();

                    // המרת הנתונים לפורמט שהלקוח מבין
                    var debtsData = sortedDebts.Select(d => new {
                        d.DebtID, d.LessonName, d.LessonType, d.LecturerName, d.IsPaid, d.IsSubmitted, d.MaterialLink, d.Hours,
                        StudentID = student.StudentID,
                        FirstName = student.FirstName,
                        LastName = student.LastName
                    });

                    return Ok(new BotResponse 
                    { 
                        Reply = sb.ToString(), // ההודעה האנושית שבנינו
                        StudentId = student.StudentID,
                        ActionType = unpaid.Any() ? "ShowDebts" : "UploadFile", // אם יש חוב כספי - מציג תשלום, אחרת ישר העלאה
                        Data = debtsData
                    });
                }

                // --- שלב ב: שיחה רגילה (נשאר ללא שינוי, רק מוודאים שעובד) ---
                var currentStudent = await _dbService.GetStudentByIdAsync(request.StudentId);
                if (currentStudent == null) return Unauthorized("שגיאת זיהוי: תלמידה לא נמצאה");

                var currentDebts = await _dbService.GetDebtsByStudentIdAsync(request.StudentId);
                
                var responseData = currentDebts.OrderBy(d => d.IsSubmitted).Select(d => new {
                    d.DebtID, d.LessonName, d.LessonType, d.LecturerName, d.IsPaid, d.IsSubmitted, d.MaterialLink, d.Hours,
                    StudentID = currentStudent.StudentID,
                    FirstName = currentStudent.FirstName,
                    LastName = currentStudent.LastName
                });

                var hasUnpaid = currentDebts.Any(d => !d.IsPaid);

                if (hasUnpaid)
                {
                    return Ok(new BotResponse 
                    { 
                        Reply = "כדי להתקדם, יש להסדיר את התשלום עבור החובות המסומנים באדום למטה.",
                        StudentId = request.StudentId,
                        ActionType = "ShowDebts",
                        Data = responseData
                    });
                }
                else
                {
                    return Ok(new BotResponse 
                    { 
                        Reply = "התשלום נקלט בהצלחה! כל האפשרויות פתוחות בפניך: ניתן להוריד את החומרים ולהעלות את העבודות כעת.",
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
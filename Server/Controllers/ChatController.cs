using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using CompletionBot.Server.Models;
using System.Text.Json;
using System.Text;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Net;
using System.Net.Mail;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        private const int MAX_QUOTA_HOURS = 300; // מכסת שעות רשות
        private const string SUPPORT_EMAIL = "learningPortal@byta.org.il";

        public ChatController(DbService dbService, IConfiguration config)
        {
            _dbService = dbService;
            _apiKey = (config["Gemini:ApiKey"] ?? "").Trim();
            _config = config;

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

            _httpClient = new HttpClient(handler);
        }

        // --- (פונקציית הורדת האישור נשארת ללא שינוי, העתקתי אותה לקובץ המלא למטה) ---
        [HttpGet("download-certificate/{studentId}")]
        public async Task<IActionResult> DownloadCertificate(string studentId)
        {
            if (string.IsNullOrEmpty(studentId)) return BadRequest("מספר זהות לא תקין");

            var student = await _dbService.GetStudentByIdAsync(studentId);
            if (student == null) return NotFound("תלמידה לא נמצאה");

            var allDebtsRaw = await _dbService.GetStudentDebtsFullDetailsAsync(studentId);
            var uniqueDebts = allDebtsRaw.GroupBy(d => (int)d.DebtID).Select(g => g.First()).ToList();
            
            // שימוש בלוגיקה החדשה גם כאן
            var planDebts = FilterDebtsLogic(uniqueDebts); 

            bool allObligationsMet = planDebts.All(d =>
            {
                var r = SafeConvertToDictionary(d);
                bool p = r.ContainsKey("IsPaid") && IsTrue(r["IsPaid"]);
                bool s = r.ContainsKey("IsSubmitted") && IsTrue(r["IsSubmitted"]);
                bool exempt = r.ContainsKey("IsExempt") && IsTrue(r["IsExempt"]);
                return (p && s) || exempt;
            });

            if (planDebts.Any() && !allObligationsMet)
            {
                return Content("<div dir='rtl'>טרם סיימת את כל החובות, לא ניתן להפיק אישור.</div>", "text/html");
            }

            string htmlContent = GenerateCertificateHtml(student, planDebts);
            byte[] fileBytes = Encoding.UTF8.GetBytes(htmlContent);
            return File(fileBytes, "text/html", $"Ishur_Sium_{studentId}.html");
        }

        [HttpPost("message")]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            try
            {
                // ==================================================
                // שלב 1: זיהוי תלמידה
                // ==================================================
                string studentIdToUse = request.StudentId;
                bool isInitialLogin = false;

                if (string.IsNullOrEmpty(studentIdToUse))
                {
                    var inputId = request.UserMessage.Trim();
                    if (!inputId.All(char.IsDigit) || inputId.Length < 8)
                        return Ok(new BotResponse { Reply = "שלום! אני מערכת אוטומטית לבדיקת זכאות. נא להקליד מספר תעודת זהות בלבד." });

                    studentIdToUse = inputId;
                    isInitialLogin = true;
                }

                var student = await _dbService.GetStudentByIdAsync(studentIdToUse);
                if (student == null)
                    return Ok(new BotResponse { Reply = "לא מצאתי תלמידה כזו במערכת. נא לוודא שהקשת תעודת זהות נכונה." });

                if (!isInitialLogin && request.UserMessage.Trim().All(char.IsDigit) && request.UserMessage.Length >= 8)
                    return Ok(new BotResponse { Reply = "אני רואה שאת כבר מחוברת. לרענון, טעני את הדף מחדש.", StudentId = studentIdToUse, ActionType = "None" });

                // ==================================================
                // שלב 2: לוגיקת הסינון החדשה (חובה + 300)
                // ==================================================
                var allDebtsRaw = await _dbService.GetStudentDebtsFullDetailsAsync(studentIdToUse);
                var uniqueDebts = allDebtsRaw.GroupBy(d => (int)d.DebtID).Select(g => g.First()).ToList();
                
                // כאן אנחנו מקבלים רק את הקורסים שצריך לבצע (העודפים סוננו החוצה בפונקציה)
                var activePlanDebts = FilterDebtsLogic(uniqueDebts);

                // בדיקת סיום חובות על הרשימה הפעילה בלבד
                bool allObligationsMet = activePlanDebts.All(d =>
                {
                    var r = SafeConvertToDictionary(d);
                    bool p = r.ContainsKey("IsPaid") && IsTrue(r["IsPaid"]);
                    bool s = r.ContainsKey("IsSubmitted") && IsTrue(r["IsSubmitted"]);
                    bool exempt = r.ContainsKey("IsExempt") && IsTrue(r["IsExempt"]);
                    return (p && s) || exempt;
                });

                // ==================================================
                // שלב 3: טיפול בסיום לימודים (בכניסה ראשונית בלבד)
                // ==================================================
                if ((!activePlanDebts.Any() || allObligationsMet) && isInitialLogin)
                {
                    return await GenerateCompletionResponse(student, activePlanDebts);
                }

                // ==================================================
                // שלב 4: הכנת נתונים לתצוגה (כולל סיווג סוג כרטיסייה)
                // ==================================================
                var debtsData = activePlanDebts.Select(d =>
                {
                    var row = SafeConvertToDictionary(d);
                    int hoursVal = 0;
                    if (row.ContainsKey("Hours") && row["Hours"] != null)
                        int.TryParse(row["Hours"].ToString(), out hoursVal);

                    string materialLink = row.ContainsKey("MaterialLink") ? (row["MaterialLink"]?.ToString() ?? "") : "";
                    string lessonType = row.ContainsKey("LessonType") ? (row["LessonType"]?.ToString() ?? "") : "";

                    // ** לוגיקה לקביעת סוג התצוגה **
                    string displayType = "Regular"; // ברירת מחדל
                    
                    // בדיקה האם זה הוראות טקסט (אין קישור או שהקישור לא נראה כמו לינק)
                    bool isUrl = materialLink.StartsWith("http") || materialLink.StartsWith("www");
                    
                    if (!string.IsNullOrEmpty(materialLink) && !isUrl)
                    {
                        displayType = "TextOnly"; // סוג ג': הוראות טקסט
                    }
                    else if (materialLink.Contains("classroom.google") || 
                             lessonType.Contains("חובה") || 
                             lessonType.Contains("מוקשבים") || 
                             lessonType.Contains("מודרכת"))
                    {
                        displayType = "Classroom"; // סוג ב': קלאסרום / חובה
                    }

                    return new
                    {
                        DebtID = row.ContainsKey("DebtID") ? row["DebtID"] : 0,
                        LessonName = row.ContainsKey("LessonName") ? row["LessonName"] : "",
                        LessonType = lessonType,
                        LecturerName = row.ContainsKey("LecturerName") ? row["LecturerName"] : "",
                        MaterialLink = materialLink,
                        IsPaid = row.ContainsKey("IsPaid") && IsTrue(row["IsPaid"]),
                        IsSubmitted = row.ContainsKey("IsSubmitted") && IsTrue(row["IsSubmitted"]),
                        Hours = hoursVal,
                        IsExempt = false,
                        DisplayType = displayType // שדה חדש לריאקט
                    };
                }).ToList();

                // ==================================================
                // שלב 5: שיחה רגילה
                // ==================================================
                string systemPrompt;
                if (isInitialLogin)
                    systemPrompt = BuildSmartSystemPrompt(student, activePlanDebts, "התלמידה נכנסה כעת למערכת.", true);
                else
                    systemPrompt = BuildSmartSystemPrompt(student, activePlanDebts, request.UserMessage, false);

                var aiReply = await GetSmartGeminiResponse(systemPrompt);

                if (aiReply.Contains("המערכת עמוסה"))
                    return Ok(new BotResponse { Reply = aiReply, StudentId = student.StudentID });

                if (isInitialLogin)
                {
                    // בכניסה ראשונית: מציגים כרטיסיות (רק של הקורסים הפעילים)
                    bool hasUnpaid = debtsData.Any(d => !d.IsPaid);
                    string actionType = hasUnpaid ? "ShowDebts" : "UploadFile";

                    return Ok(new BotResponse
                    {
                        Reply = aiReply,
                        StudentId = student.StudentID,
                        ActionType = actionType,
                        Data = debtsData
                    });
                }
                else
                {
                    // המשך שיחה: ללא כרטיסיות
                    return Ok(new BotResponse
                    {
                        Reply = aiReply,
                        StudentId = student.StudentID,
                        ActionType = "None",
                        Data = null
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CRITICAL ERROR: " + ex.Message);
                return StatusCode(200, new BotResponse { Reply = $"אירעה תקלה במערכת. נא לפנות למייל: {SUPPORT_EMAIL}", ActionType = "Error" });
            }
        }
// ====================================================================================
// === לוגיקת הסינון המתוקנת (כולל חובה, מודרכת ומוקשבים) ===
// ====================================================================================
private List<dynamic> FilterDebtsLogic(IEnumerable<dynamic> allDebts)
{
    var rawList = allDebts.ToList();
    var finalSocket = new List<dynamic>();
    
    // רשימות עזר
    var mandatoryCourses = new List<dynamic>(); // קורסים שתמיד חייבים
    var electiveCourses = new List<dynamic>();  // קורסים שכפופים ל-300 שעות

    // שלב 1: מיון - מי חובה ומי רשות?
    foreach (var debt in rawList)
    {
        var row = SafeConvertToDictionary(debt);
        
        // קודם כל, אם יש פטור מפורש במסד הנתונים - מכבדים אותו
        bool isExempt = row.ContainsKey("IsExempt") && IsTrue(row["IsExempt"]);
        if (isExempt) continue;

        string type = row.ContainsKey("LessonType") ? (row["LessonType"]?.ToString() ?? "") : "";
        
        // --- התיקון הגדול כאן: הרחבנו את התנאי ---
        // אם זה חובה, או מוקשבים, או מודרכת -> זה נכנס תמיד לרשימה!
        if (type.Contains("חובה") || 
            type.Contains("מודרכת") || 
            type.Contains("מוקשבים") || 
            type.Contains("מתוקשב")) 
        {
            mandatoryCourses.Add(debt);
        }
        else
        {
            // כל השאר הולכים לרשימת ההמתנה של ה-300 שעות
            electiveCourses.Add(debt);
        }
    }

    // שלב 2: הוספת כל קורסי החובה/מודרכת/מוקשבים (הם תמיד בפנים!)
    finalSocket.AddRange(mandatoryCourses);

    // שלב 3: מילוי מכסה של 300 שעות מקורסי הרשות בלבד
    int accumulatedElectiveHours = 0;
    
    foreach (var debt in electiveCourses)
    {
        var row = SafeConvertToDictionary(debt);
        int hours = 0;
        if (row.ContainsKey("Hours") && row["Hours"] != null) 
            int.TryParse(row["Hours"]?.ToString(), out hours);

        // אם הוספת הקורס הזה תחרוג משמעותית מהמכסה (נתתי באפר קטן של 50)
        if (accumulatedElectiveHours > 0 && (accumulatedElectiveHours + hours) > (MAX_QUOTA_HOURS + 50)) 
        {
            continue; // הקורס הזה בחוץ
        }

        // אם כבר הגענו למכסה
        if (accumulatedElectiveHours >= MAX_QUOTA_HOURS) 
        {
            continue;
        }

        finalSocket.Add(debt);
        accumulatedElectiveHours += hours;
    }

    return finalSocket;
}
        // --- (שאר פונקציות העזר: BuildSmartSystemPrompt, GenerateCertificate וכו' נשארות זהות לקוד הקודם ששלחתי לך) ---
        // (העתקתי אותן כאן שוב לנוחותך כדי שהקובץ יהיה שלם)

        private string BuildSmartSystemPrompt(dynamic student, IEnumerable<dynamic> debts, string userMessage, bool isInitial)
        {
            var debtsList = debts.ToList();
            var allRawData = new List<Dictionary<string, object?>>();

            foreach (var debt in debtsList)
            {
                var fullRow = SafeConvertToDictionary(debt);
                if (fullRow.ContainsKey("UploadDate") && fullRow["UploadDate"] != null)
                {
                    if (DateTime.TryParse(fullRow["UploadDate"]?.ToString(), out DateTime dt))
                        fullRow["AI_Readable_Date"] = dt.ToString("dd/MM/yyyy");
                }
                allRawData.Add(fullRow);
            }

            var contextData = new
            {
                StudentName = $"{student.FirstName} {student.LastName}",
                FullDatabaseRecords = allRawData
            };

            string jsonString = JsonSerializer.Serialize(contextData, new JsonSerializerOptions { WriteIndented = true });
            var sb = new StringBuilder();

            sb.AppendLine("הגדרת תפקיד: את מזכירה אדיבה, מכבדת ומקצועית ב'בית המורה' (סמינר שצ'רנסקי).");
            sb.AppendLine("סגנון: עברית תקנית, מכבדת ונעימה.");
            sb.AppendLine("איסורים: אל תשתמשי במילה 'מכללה', רק 'בית המורה' או 'הסמינר'.");
            
            sb.AppendLine("!!! הוראת ברזל !!!");
            sb.AppendLine("אסור להציג טבלאות. אל תשתמשי בסימנים כמו |---| ואל תציגי רשימות טכניות. דברי בשפה טבעית.");
            sb.AppendLine("תשובות לנושאים זרים: אם התלמידה שואלת על נושא שלא קשור ללימודים (כמו מזג אוויר, חדשות וכו') - עני בנימוס שאת יכולה לעזור רק בנושאי הלימודים.");

            sb.AppendLine("");
            sb.AppendLine("--- נתונים מלאים על התיק האישי (JSON) ---");
            sb.AppendLine(jsonString);
            sb.AppendLine("----------------------------------");
            
            sb.AppendLine("הנחיות לשימוש בנתונים:");
            sb.AppendLine("1. ה-JSON מכיל את כל השדות ממסד הנתונים. חפשי שם תשובות לכל שאלה.");
            sb.AppendLine("2. השדה 'UploadDate' או 'AI_Readable_Date' מייצג את תאריך ההגשה.");

            sb.AppendLine("");

            if (isInitial)
            {
                sb.AppendLine("זוהי תחילת השיחה.");
                sb.AppendLine($"1. ברכי לשלום את {student.FirstName}.");
                sb.AppendLine("2. תני סקירה קצרה ונעימה על המצב, בלי לפרט רשימות ארוכות מדי.");
            }
            else
            {
                sb.AppendLine("!!! הוראה קריטית להמשך שיחה !!!");
                sb.AppendLine("התלמידה כבר נמצאת באמצע שיחה רציפה.");
                sb.AppendLine("1. **איסור מוחלט לפנות בשם התלמידה**.");
                sb.AppendLine("   - אל תכתבי 'גילה יקרה', אל תכתבי 'שלום', ואל תכתבי שום מילות פתיחה.");
                sb.AppendLine("   - התחילי ישר בתשובה העניינית.");
                sb.AppendLine("2. דוגמה טובה לתשובה: 'העבודה בפסיכולוגיה הוגשה בתאריך 10/12/2025 וקיבלה ציון 90'.");
                sb.AppendLine($"שאלה אחרונה של התלמידה: \"{userMessage}\"");
                sb.AppendLine("השתמשי בנתונים למעלה כדי לתת תשובה מדויקת.");
            }

            return sb.ToString();
        }

        private async Task<string> GetSmartGeminiResponse(string prompt)
        {
            if (string.IsNullOrEmpty(_apiKey)) return "חסר מפתח AI.";
            string[] models = { "gemini-2.0-flash", "gemini-2.0-flash-001", "gemini-2.5-flash" };

            foreach (var modelName in models)
            {
                try
                {
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_apiKey}";
                    var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { temperature = 0.15, maxOutputTokens = 2048 } };
                    var json = JsonSerializer.Serialize(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(url, content);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(responseString);
                        var candidates = doc.RootElement.GetProperty("candidates");
                        if (candidates.GetArrayLength() > 0)
                        {
                            var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                return text.Replace("**", "").Replace("###", "").Replace("|", "").Replace("```json", "").Replace("```", "");
                            }
                        }
                    }
                    else if ((int)response.StatusCode == 429) continue;
                }
                catch (Exception) { }
            }
            return "המערכת עמוסה כרגע, נסי להכנס שוב מאוחר יותר.";
        }

        private async Task<IActionResult> GenerateCompletionResponse(dynamic student, List<dynamic> completedDebts)
        {
            _ = SendCertificateEmail(student, completedDebts);

            var req = HttpContext.Request;
            string baseUrl = $"{req.Scheme}://{req.Host}";
            string downloadUrl = $"{baseUrl}/api/Chat/download-certificate/{student.StudentID}";

            string buttonHtml = $@"<br><br>
            <div style='text-align:center; margin-top: 15px;'>
                <a href='{downloadUrl}' target='_blank' style='
                    background: linear-gradient(to bottom right, #b47d38, #dcb878);
                    color: white; padding: 8px 20px; text-decoration: none; border-radius: 6px;
                    font-weight: bold; font-size: 14px; box-shadow: 0 2px 5px rgba(180, 125, 56, 0.3);
                    display: inline-flex; align-items: center; gap: 8px;'>
                   <span>הורדת אישור סיום חובות לימודיים</span>
                </a>
            </div>";

            string completionPrompt = $"התלמידה {student.FirstName} סיימה הכל כרגע. כתבי ברכה קצרה, מכבדת ומקצועית (2 משפטים) על הסיום ושהאישור נשלח למזכירות.";
            var aiReply = await GetSmartGeminiResponse(completionPrompt);

            return Ok(new BotResponse
            {
                Reply = aiReply + buttonHtml,
                StudentId = student.StudentID,
                ActionType = "None",
                Data = null
            });
        }

        private string GenerateCertificateHtml(dynamic student, IEnumerable<dynamic> completedCourses)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='UTF-8'></head><body style='font-family: Arial, sans-serif;'>");
            sb.AppendLine("<div style='border: 2px solid #003366; padding: 20px; max-width: 800px; margin: 0 auto;'>");
            sb.AppendLine("<div style='text-align: center; margin-bottom: 20px;'>");
            sb.AppendLine($"<h1 style='color:#003366;'>אישור סיום חובות לימודיים</h1>");
            sb.AppendLine($"<h3>בית המורה - סמינר שצ'רנסקי</h3>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<p><strong>שם התלמידה:</strong> {student.FirstName} {student.LastName}</p>");
            sb.AppendLine($"<p><strong>תעודת זהות:</strong> {student.StudentID}</p>");
            sb.AppendLine($"<p><strong>תאריך הפקה:</strong> {DateTime.Now:dd/MM/yyyy}</p>");
            sb.AppendLine("<hr>");
            sb.AppendLine("<h3>פירוט הקורסים שהושלמו:</h3>");
            sb.AppendLine("<table border='1' cellspacing='0' cellpadding='5' style='border-collapse: collapse; width:100%; text-align: right;'>");
            sb.AppendLine("<tr style='background-color: #f2f2f2;'><th>מטרת לימודים</th><th>מס' שיעור</th><th>שם השיעור</th><th>מרצה</th><th>סטטוס</th></tr>");

            foreach (var item in completedCourses)
            {
                var row = SafeConvertToDictionary(item);
                string lessonNum = row.ContainsKey("LessonNumber") ? (row["LessonNumber"]?.ToString() ?? "") : "";
                string studyGoal = row.ContainsKey("StudyGoal") ? (row["StudyGoal"]?.ToString() ?? "") : "";
                string name = row.ContainsKey("LessonName") ? (row["LessonName"]?.ToString() ?? "") : "";
                string lecturer = row.ContainsKey("LecturerName") ? (row["LecturerName"]?.ToString() ?? "") : "";
                sb.AppendLine($"<tr><td>{studyGoal}</td><td>{lessonNum}</td><td>{name}</td><td>{lecturer}</td><td>הושלם</td></tr>");
            }
            sb.AppendLine("</table>");
            sb.AppendLine("<br><p>מסמך זה הופק באופן ממוחשב.</p>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private async Task SendCertificateEmail(dynamic student, IEnumerable<dynamic> completedCourses)
        {
            try
            {
                var smtpHost = _config["Smtp:Host"];
                int smtpPort = 587;
                if (!string.IsNullOrEmpty(_config["Smtp:Port"])) int.TryParse(_config["Smtp:Port"], out smtpPort);
                var smtpUser = _config["Smtp:User"];
                var smtpPass = _config["Smtp:Pass"];

                if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(smtpUser)) return;

                string htmlBody = GenerateCertificateHtml(student, completedCourses);

                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(smtpUser, "Learning Portal System");
                    message.To.Add(SUPPORT_EMAIL);
                    message.Subject = $"אישור סיום חובות - {student.FirstName} {student.LastName} ({student.StudentID})";
                    message.Body = htmlBody;
                    message.IsBodyHtml = true;

                    using (var client = new SmtpClient(smtpHost, smtpPort))
                    {
                        client.EnableSsl = true;
                        client.Credentials = new NetworkCredential(smtpUser, smtpPass);
                        await client.SendMailAsync(message);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Failed to send email: {ex.Message}"); }
        }

        private Dictionary<string, object?> SafeConvertToDictionary(object obj)
        {
            if (obj == null) return new Dictionary<string, object?>();
            if (obj is IDictionary<string, object> dict) return dict.ToDictionary(k => k.Key, k => (object?)k.Value);

            var result = new Dictionary<string, object?>();
            foreach (var prop in obj.GetType().GetProperties())
            {
                try { result[prop.Name] = prop.GetValue(obj); } catch { }
            }
            return result;
        }

        private bool IsTrue(object? val)
        {
            if (val == null) return false;
            string s = val.ToString()?.ToLower()?.Trim() ?? "";
            return s == "true" || s == "1" || s == "yes";
        }
    }
}
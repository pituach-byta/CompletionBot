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
        private const string SUPPORT_EMAIL = "botseminr@byta.org.il";

        public ChatController(DbService dbService, IConfiguration config)
        {
            _dbService = dbService;
            _apiKey = (config["Gemini:ApiKey"] ?? "").Trim();
            _config = config;

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

            _httpClient = new HttpClient(handler);
        }
[HttpGet("download-certificate/{studentId}")]
public async Task<IActionResult> DownloadCertificate(string studentId)
{
    if (string.IsNullOrEmpty(studentId)) return BadRequest("מספר זהות לא תקין");

    var student = await _dbService.GetStudentByIdAsync(studentId);
    if (student == null) return NotFound("תלמידה לא נמצאה");

    var allDebtsRaw = await _dbService.GetStudentDebtsFullDetailsAsync(studentId);
    var uniqueDebts = allDebtsRaw.GroupBy(d => (int)d.DebtID).Select(g => g.First()).ToList();
    
    // זה השלב הקריטי שמחשב את ה-IsAllowedSubmission (מכסת 300 שעות) ואת ה-IsInstructionsOnly
    var planDebts = FilterDebtsLogic(uniqueDebts); 

    bool hasPendingRealTasks = planDebts.Any(d => {
        var r = SafeConvertToDictionary(d);
        bool isExempt = IsTrue(r["IsExempt"]);
        bool isAllowed = r.ContainsKey("IsAllowedSubmission") ? IsTrue(r["IsAllowedSubmission"]) : true;
        bool isInstructionsOnly = IsTrue(r["IsInstructionsOnly"]);
        bool isFinished = IsTrue(r["IsPaid"]) && IsTrue(r["IsSubmitted"]);

        // חוסמים רק אם זו מטלה רגילה בבוט שטרם בוצעה
        return isAllowed && !isExempt && !isInstructionsOnly && !isFinished;
    });

    if (hasPendingRealTasks)
    {
        return Content("<div dir='rtl' style='font-family:sans-serif;'>קיימות מטלות שטרם הוגשו בבוט. לא ניתן להפיק אישור עד לסיום העלאת הקבצים ותשלום עבורן.</div>", "text/html");
    }

    // שולחים את planDebts (שכבר מכיל את כל החישובים) לפונקציית ה-HTML
    string htmlContent = GenerateCertificateHtml(student, planDebts);
    byte[] fileBytes = Encoding.UTF8.GetBytes(htmlContent);
    return File(fileBytes, "text/html", $"Ishur_Sium_{studentId}.html");
}
        [HttpPost("message")]
public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
{
    try
    {
        // 1. זיהוי תלמידה (נשאר ללא שינוי)
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

        // 2. לוגיקת הסינון
        var allDebtsRaw = await _dbService.GetStudentDebtsFullDetailsAsync(studentIdToUse);
        var uniqueDebts = allDebtsRaw.GroupBy(d => (int)d.DebtID).Select(g => g.First()).ToList();
        var activePlanDebts = FilterDebtsLogic(uniqueDebts);

        // --- תוספת: חישוב הסכום הכולל לתשלום ---
        decimal totalToPayNow = activePlanDebts
            .Where(d => {
                var r = SafeConvertToDictionary(d);
                return !(r.ContainsKey("IsPaid") && IsTrue(r["IsPaid"])) && 
                       !(r.ContainsKey("IsExempt") && IsTrue(r["IsExempt"]));
            })
            .Sum(d => {
                var r = SafeConvertToDictionary(d);
                return (decimal)GetLessonPrice(
                    r["LessonType"]?.ToString() ?? "",
                    r.ContainsKey("LessonName") ? r["LessonName"]?.ToString() ?? "" : "");
            });

        // בדיקת סיום חובות
        // קורסים שיש להם רק הוראות לא מעכבים
        // ONLY קורסים שהם "מתוקשב"/"מודרכת" וחרגו מהמכסה לא מעכבים
        bool allObligationsMet = activePlanDebts.All(d =>
        {
            var r = SafeConvertToDictionary(d);
            bool isInstructionsOnly = r.ContainsKey("IsInstructionsOnly") && IsTrue(r["IsInstructionsOnly"]);
            
            // אם זה הוראות בלבד - לא מעכב
            if (isInstructionsOnly)
                return true;
            
            // בדוק אם זה קורס "מתוקשב"/"מודרכת" שחרג מהמכסה
            string type = r.ContainsKey("LessonType") ? (r["LessonType"]?.ToString() ?? "") : "";
            bool isCapLimitedType = type.Contains("מתוקשב") || type.Contains("מודרכת");
            bool isAllowedSubmission = !r.ContainsKey("IsAllowedSubmission") || IsTrue(r["IsAllowedSubmission"]);
            
            // אם זה קורס "מתוקשב"/"מודרכת" שחרג מהמכסה - לא מעכב
            if (isCapLimitedType && !isAllowedSubmission)
                return true;
            
            // כל קורס אחר: צריך להיות משולם והוגש (או פטור ידני)
            bool p = r.ContainsKey("IsPaid") && IsTrue(r["IsPaid"]);
            bool s = r.ContainsKey("IsSubmitted") && IsTrue(r["IsSubmitted"]);
            bool exempt = r.ContainsKey("IsExempt") && IsTrue(r["IsExempt"]);
            
            return (p && s) || exempt;
        });

        if ((!activePlanDebts.Any() || allObligationsMet) && isInitialLogin)
        {
            return await GenerateCompletionResponse(student, activePlanDebts);
        }

        // 4. הכנת נתונים לתצוגה (שמרתי על כל השדות המקוריים שלך!)
        var debtsData = activePlanDebts.Select(d =>
        {
            var row = SafeConvertToDictionary(d);
            int hoursVal = 0;
            if (row.ContainsKey("Hours") && row["Hours"] != null)
                int.TryParse(row["Hours"].ToString(), out hoursVal);

            string materialLink = row.ContainsKey("MaterialLink") ? (row["MaterialLink"]?.ToString() ?? "") : "";
            string lessonType = row.ContainsKey("LessonType") ? (row["LessonType"]?.ToString() ?? "") : "";

            // לוגיקת קביעת סוג התצוגה
            string displayType = "Regular";
            bool isUrl = materialLink.StartsWith("http") || materialLink.StartsWith("www");
            
            if (!string.IsNullOrEmpty(materialLink) && !isUrl) displayType = "TextOnly";
            else if (materialLink.Contains("classroom.google") || 
                     lessonType.Contains("חובה") || 
                     lessonType.Contains("מתוקשב") || 
                     lessonType.Contains("מודרכת") ||
                     lessonType.Contains("עבודה מעשית"))
            {
                displayType = "Classroom";
            }
            else if (!isUrl && string.IsNullOrEmpty(materialLink))
            {
                // קורסים ללא קישור שאינם בקטגוריה מוגדרת - ברירת מחדל: העלאת קובץ
                displayType = "Classroom";
            }

            return new
            {
                DebtID = row.ContainsKey("DebtID") ? row["DebtID"] : 0,
                StudentID = studentIdToUse,
                LessonName = row.ContainsKey("LessonName") ? row["LessonName"] : "",
                LessonType = lessonType,
                LessonNumber = row.ContainsKey("LessonNumber") ? row["LessonNumber"] : 0,
                LecturerName = row.ContainsKey("LecturerName") ? row["LecturerName"] : "",
                MaterialLink = materialLink,
                IsPaid = row.ContainsKey("IsPaid") && IsTrue(row["IsPaid"]),
                IsSubmitted = row.ContainsKey("IsSubmitted") && IsTrue(row["IsSubmitted"]),
                Hours = hoursVal,
                IsExempt = row.ContainsKey("IsExempt") && IsTrue(row["IsExempt"]),
                DisplayType = displayType,
                Price = GetLessonPrice(lessonType, row.ContainsKey("LessonName") ? row["LessonName"]?.ToString() ?? "" : "") ,
                IsAllowedSubmission = row.ContainsKey("IsAllowedSubmission") ? row["IsAllowedSubmission"] : true,
                IsInstructionsOnly = row.ContainsKey("IsInstructionsOnly") ? row["IsInstructionsOnly"] : false
            };
        }).ToList();

        // 5. שיחה רגילה (עדכון השליחה לבוט)
        string systemPrompt;
        if (isInitialLogin)
            systemPrompt = BuildSmartSystemPrompt(student, activePlanDebts, "התלמידה נכנסה כעת למערכת.", true, totalToPayNow);
        else
            systemPrompt = BuildSmartSystemPrompt(student, activePlanDebts, request.UserMessage, false, totalToPayNow);

        var aiReply = await GetSmartGeminiResponse(systemPrompt);

        if (aiReply.Contains("המערכת עמוסה"))
            return Ok(new BotResponse { Reply = aiReply, StudentId = student.StudentID });

        if (isInitialLogin)
        {
            bool hasUnpaid = debtsData.Any(d => !d.IsPaid);
            string actionType = hasUnpaid ? "ShowDebts" : "UploadFile";
            return Ok(new BotResponse { Reply = aiReply, StudentId = student.StudentID, FirstName = student.FirstName, LastName = student.LastName, ActionType = actionType, Data = debtsData });
        }
        else
        {
            return Ok(new BotResponse { Reply = aiReply, StudentId = student.StudentID, ActionType = "None", Data = null });
        }
    }
    catch (Exception ex)
{
    Console.WriteLine($"Error in SendMessage: {ex.Message}"); // הנה השימוש ב-ex!
    return StatusCode(200, new BotResponse { Reply = $"אירעה תקלה במערכת.", ActionType = "Error" });
}
}
[HttpGet("payment-callback")]
public async Task<IActionResult> PaymentCallback([FromQuery] string ClientData, [FromQuery] string ReplyCode)
{
    if (ReplyCode != "000") return Ok("Error");
    if (string.IsNullOrEmpty(ClientData)) return BadRequest();

    try
    {
        // פיצול המזהים למקרה ששולמו כמה חובות יחד (למשל "101,102")
        var ids = ClientData.Split(',');
        foreach (var idStr in ids)
        {
            if (int.TryParse(idStr, out int debtId))
            {
                await _dbService.MarkDebtAsPaidAsync(debtId, "NedarimPlus_" + DateTime.Now.Ticks);
            }
        }

        return Ok("OK");
    }
    catch (Exception ex)
    {
        return StatusCode(500, ex.Message);
    }
}

/// <summary>
/// לוגיקת סינון חובות תלמידה:
/// 1. קורסי "חובה", "עבודה מעשית" או "הוראות בלבד" - תמיד פתוחים להגשה.
/// 2. קורסים רגילים עם לינק (גוגל דוקס, PDF וכו') - תמיד פתוחים להגשה.
/// 3. קורסי "מתוקשב" או "מודרכת" - פתוחים להגשה רק עד מכסה מצטברת של 300 שעות.
/// </summary>
private List<dynamic> FilterDebtsLogic(IEnumerable<dynamic> allDebts)
{
    var resultList = new List<dynamic>();
    double accumulatedCappedHours = 0; // מונה שעות לקורסים המוגבלים בלבד

    // 1. מיון קבוע לפי DebtID כדי להבטיח עקביות בחישוב המכסה
    var sortedDebts = allDebts.OrderBy(d => {
        var dict = SafeConvertToDictionary(d);
        return dict.ContainsKey("DebtID") ? Convert.ToInt32(dict["DebtID"]) : 0;
    }).ToList();

    foreach (var debt in sortedDebts)
    {
        var row = SafeConvertToDictionary(debt);
        
        // שליפת נתונים בסיסיים מהשורה
        string type = row.ContainsKey("LessonType") ? (row["LessonType"]?.ToString() ?? "") : "";
        string materialLink = row.ContainsKey("MaterialLink") ? (row["MaterialLink"]?.ToString() ?? "") : "";
        
        // ניקוי וזיהוי סוג הקישור (URL לעומת טקסט חופשי)
        string linkTrimmed = materialLink.Trim();
        bool isUrl = linkTrimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) || 
                     linkTrimmed.StartsWith("www", StringComparison.OrdinalIgnoreCase);

        // אם יש תוכן בקישור אבל הוא לא URL, סימן שאלו הוראות כתובות בלבד
        bool isInstructionsOnly = !string.IsNullOrEmpty(linkTrimmed) && !isUrl;
        row["IsInstructionsOnly"] = isInstructionsOnly;

        // === לוגיקת החלטה לפי סדר עדיפויות (מניעת התנגשויות) ===

        // שלב א': בדיקת קטגוריה 3 (מתוקשב/מודרכת)
        // שלב זה קודם לכל כדי שגם אם יש לינק, הקורס עדיין ייספר במכסה
        if (type.Contains("מתוקשב") || type.Contains("מודרכת"))
        {
            double hours = 0;
            if (row.ContainsKey("Hours") && row["Hours"] != null)
                double.TryParse(row["Hours"]?.ToString(), out hours);

            if (accumulatedCappedHours < MAX_QUOTA_HOURS)
            {
                row["IsAllowedSubmission"] = true;
                accumulatedCappedHours += hours; // הוספה למכסה
            }
            else
            {
                // חריגה מהמכסה - הקורס לא יוצג להגשה (אבל יישאר לתשלום)
                row["IsAllowedSubmission"] = false; 
            }
        }
        
        // שלב ב': בדיקת קטגוריות 1 ו-2 (חובה, מעשי, הוראות בלבד, או לינק לקובץ)
        // אלו קורסים שתמיד פתוחים ואינם נספרים במכסת ה-300
        else if (type.Contains("חובה") || 
                 type.Contains("עבודה מעשית") || 
                 isInstructionsOnly || 
                 isUrl) // כאן נכנסים הלינקים ששלחת (גוגל דוקס/PDF)
        {
            row["IsAllowedSubmission"] = true;
        }

        // שלב ג': ברירת מחדל לכל מקרה אחר
        else
        {
            row["IsAllowedSubmission"] = true;
        }
        
        resultList.Add(row);
    }

    return resultList;
}
       private string BuildSmartSystemPrompt(dynamic student, IEnumerable<dynamic> debts, string userMessage, bool isInitial, decimal totalAmount)
{
    var debtsList = debts.ToList();
    var allRawData = new List<Dictionary<string, object?>>();

    foreach (var debt in debtsList)
    {
        var fullRow = SafeConvertToDictionary(debt);
        
        // לוגיקה חדשה: זיהוי האם הקישור הוא הוראות בלבד (טקסט) או לינק להגשה (URL)
        string link = fullRow.ContainsKey("MaterialLink") ? fullRow["MaterialLink"]?.ToString() ?? "" : "";
        bool isRealUrl = link.StartsWith("http") || link.StartsWith("www");
        fullRow["IsInstructionsOnly"] = !string.IsNullOrEmpty(link) && !isRealUrl;

        if (fullRow.ContainsKey("UploadDate") && fullRow["UploadDate"] != null)
        {
            if (DateTime.TryParse(fullRow["UploadDate"]?.ToString(), out DateTime dt))
                fullRow["AI_Readable_Date"] = dt.ToString("dd/MM/yyyy");
        }
        allRawData.Add(fullRow);
    }

    var contextData = new
    {
        StudentName = student.FirstName, // שימוש בשם פרטי לפנייה נעימה
        FullDatabaseRecords = allRawData
    };

    string jsonString = JsonSerializer.Serialize(contextData, new JsonSerializerOptions { WriteIndented = true });
    var sb = new StringBuilder();

    // --- 1. הגדרת זהות ועקרונות אישיות (החלק ה"אנושי") ---
    sb.AppendLine("הגדרת תפקיד: את מזכירה אדיבה, מכבדת ומקצועית ב'בית המורה' (סמינר שצ'רנסקי).");
    sb.AppendLine("סגנון שיחה: דברי בצורה טבעית, מגוונת ואנושית. אל תחזרי על נוסחים קבועים. התאימו את עצמך לסיטואציה של התלמידה.");
    sb.AppendLine("שפה: עברית תקנית ונעימה. איסור מוחלט להשתמש במילה 'מכללה', רק 'בית המורה' או 'הסמינר'.");
    sb.AppendLine("נושאים זרים: עני בנימוס שאת מסייעת רק בנושאי הלימודים במידה והתלמידה שואלת על נושאים אחרים.");

    // --- 2. חוקי ברזל טכניים (מניעת סרבול) ---
    sb.AppendLine("");
    sb.AppendLine("!!! חוקי הגשה ותצוגה !!!");
    sb.AppendLine("- איסור טבלאות: אל תשתמשי בסימנים כמו |---| או רשימות טכניות. השתמשי בפסקאות טבעיות.");
    sb.AppendLine("- מניעת הכבדה: אל תצייני את שנת הלימודים, הקבוצה או מטרת הלימודים של התלמידה. זה מידע מיותר עבורה.");
    sb.AppendLine("- דיוק בסטטוס: אם בשדה 'IsInstructionsOnly' מופיע True, המשמעות היא שהתלמידה רואה הנחיות לביצוע בלבד. אל תברכי אותה על הגשת העבודה!.");

    // --- תוספת הנחיה למזכירה הדיגיטלית ---
sb.AppendLine("!!! הנחיה קריטית לקורסי 'הוראות בלבד' !!!");
sb.AppendLine("במידה ובשדה 'IsInstructionsOnly' מופיע True: אל תגידי לתלמידה 'הגשת' או 'סיימת'.");
sb.AppendLine("הסבירי לה שעליה לבצע את ההוראות המופיעות בתיאור וליצור קשר עם הגורם האחראי כדי שהחוב ייסגר סופית.");

    // --- 3. נתונים כספיים ---
    sb.AppendLine("");
    sb.AppendLine("!!! נתון כספי !!!");
    sb.AppendLine("-אל תכתבי בשום אופן סכום מדויק לתשלום זה שמור במזכירות!!!");
    sb.AppendLine("- הנחיה: אם התלמידה טרם שילמה, הציגי זאת בעדינות כצעד ראשון והכרחי לפני השלמת העבודות בפועל. אבל אל תכתבי בשום אופן את הסכום לשתלום זה פרט שאת לא יודעת!!! ");

    // --- 4. הזרקת נתוני המסד (JSON) ---
    sb.AppendLine("");
    sb.AppendLine("--- נתוני התיק האישי מהמסד (JSON) ---");
    sb.AppendLine(jsonString);
    sb.AppendLine("----------------------------------");

    // --- 5. לוגיקת שיחה (פתיחה מול המשך) ---
    if (isInitial)
    {
        sb.AppendLine("");
        sb.AppendLine("הנחיות לכניסה ראשונה:");
        sb.AppendLine($"1. פתחי בברכה לבבית ואדיבה ל{student.FirstName}.");
        sb.AppendLine("2. תני סקירה קצרה עד 4 משפטים כללית, נעימה ויעילה על המצב והסבר קצרצר על תפקידך כמזכריה דיגיטלית להשלמת עבודות. אל תפרטי רשימות קורסים ארוכות או שמות קורסים או תאריכים מפורטים מיד בהתחלה.");
        sb.AppendLine("3. הסבירי בקצרה שבאופן עקרוני לאחר הסדרת התשלום נוכל להתקדם להשלמת כל המטלות.");
    }
    else
    {
        sb.AppendLine("");
        sb.AppendLine("!!! הנחיות להמשך שיחה !!!");
        sb.AppendLine("1. איסור פנייה בשם: אל תכתבי 'שלום' או את שם התלמידה. עני ישר ולעניין.");
        sb.AppendLine("2. זיהוי התקדמות: אם את מזהה בנתונים שהתלמידה השלימה חוב מאז הפעם האחרונה, צייני זאת לטובה בטבעיות.");
        sb.AppendLine("3. טיפול בתעודת זהות: אם התלמידה הקלידה שוב מספר זהות, הסבירי לה בנעימות שהיא כבר מחוברת למערכת.");
        sb.AppendLine($"שאלה אחרונה של התלמידה: \"{userMessage}\"");
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
    // --- הוספה חדשה ובטוחה: עדכון סטטוס התלמידה ל-'Completed' ---
    await _dbService.UpdateStudentStatusAsync(student.StudentID, "Completed");

    // --- התיקון כאן: הוספת await ---
    // במקום: _ = SendCertificateEmail(student, completedDebts);
    await SendCertificateEmail(student, completedDebts); 

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
        FirstName = student.FirstName,
        LastName = student.LastName,
        ActionType = "None",
        Data = null
    });
}
 private string GenerateCertificateHtml(dynamic student, IEnumerable<dynamic> completedCourses)
{
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='UTF-8'></head><body style='font-family: Arial, sans-serif;'>");
    sb.AppendLine("<div style='border: 2px solid #003366; padding: 20px; max-width: 800px; margin: 0 auto;'>");
    
    // כותרת
    sb.AppendLine("<div style='text-align: center; margin-bottom: 20px;'><h1 style='color:#003366;'>אישור מצב חובות לימודיים</h1><h3>בית המורה - סמינר שצ'רנסקי</h3></div>");

    // פרטי תלמידה
    sb.AppendLine($"<p><strong>שם התלמידה:</strong> {student.FirstName} {student.LastName}</p>");
    sb.AppendLine($"<p><strong>תעודת זהות:</strong> {student.StudentID}</p>");
    sb.AppendLine($"<p><strong>תאריך הפקה:</strong> {DateTime.Now:dd/MM/yyyy}</p><hr>");
    
    sb.AppendLine("<h3>פירוט סטטוס קורסים:</h3>");
    sb.AppendLine("<table border='1' cellspacing='0' cellpadding='5' style='border-collapse: collapse; width:100%; text-align: right;'>");
    sb.AppendLine("<tr style='background-color: #f2f2f2;'><th>מטרת לימודים</th><th>מס' שיעור</th><th>שם השיעור</th><th>מרצה</th><th>סטטוס</th></tr>");

    foreach (var item in completedCourses)
    {
        var row = SafeConvertToDictionary(item);
        
        string lessonNum = row.ContainsKey("LessonNumber") ? row["LessonNumber"]?.ToString() ?? "" : "";
        string studyGoal = row.ContainsKey("StudyGoal") ? row["StudyGoal"]?.ToString() ?? "" : "";
        string name = row.ContainsKey("LessonName") ? row["LessonName"]?.ToString() ?? "" : "";
        string lecturer = row.ContainsKey("LecturerName") ? row["LecturerName"]?.ToString() ?? "" : "";

        // הגדרת המשתנים (משתמש ב-IsTrue המקורית שלך)
        bool isFinished = IsTrue(row["IsPaid"]) && IsTrue(row["IsSubmitted"]);
        bool isExemptManual = row.ContainsKey("IsExempt") && IsTrue(row["IsExempt"]);
        bool isAllowed = !row.ContainsKey("IsAllowedSubmission") || IsTrue(row["IsAllowedSubmission"]);
        bool isInstructionsOnly = row.ContainsKey("IsInstructionsOnly") && IsTrue(row["IsInstructionsOnly"]);

        string statusText = "";

        // --- לוגיקה חזקה וסופית לפי סדר העדיפויות שביקשת ---

        // 1. קודם כל: הוראות בלבד - תמיד יוצג כ"לא הושלם" באדום (מתקן את 2000167)
        if (isInstructionsOnly)
        {
            statusText = "<span style='color: red; font-weight: bold;'>לא הושלם</span>";
        }
        // 2. שנית: כל מה ששולם והוגש בפועל
        else if (isFinished)
        {
            statusText = "הושלם";
        }
        // 3. שלישית: כל מה שפטור (ידני או בגלל מכסה )
        else if (isExemptManual || !isAllowed)
        {
            statusText = "פטור מהגשה";
        }
        // 4. ברירת מחדל
        else
        {
            statusText = "הושלם";
        }

        sb.AppendLine($"<tr><td>{studyGoal}</td><td>{lessonNum}</td><td>{name}</td><td>{lecturer}</td><td>{statusText}</td></tr>");
    }

    sb.AppendLine("</table><br><p>מסמך זה מציג את תמונת המצב של החובות הלימודיים נכון לרגע זה.</p></div></body></html>");
    return sb.ToString();
}
 // *** החליפי את הפונקציה SendCertificateEmail המקורית שלך בקוד הזה ***

private async Task SendCertificateEmail(dynamic student, IEnumerable<dynamic> completedCourses)
{
    try
    {
        // קריאת הגדרות SMTP
        var smtpHost = _config["Smtp:Host"];
        var smtpPortStr = _config["Smtp:Port"];
        var smtpUser = _config["Smtp:User"];
        var smtpPass = _config["Smtp:Pass"];

        // לוגים לדיבאג - יעזרו לך לראות אם ההגדרות נקראו נכון
        Console.WriteLine("=== שליחת מייל - בדיקת הגדרות ===");
        Console.WriteLine($"Host: {smtpHost ?? "❌ חסר"}");
        Console.WriteLine($"Port: {smtpPortStr ?? "587 (ברירת מחדל)"}");
        Console.WriteLine($"User: {smtpUser ?? "❌ חסר"}");
        Console.WriteLine($"Pass: {(string.IsNullOrEmpty(smtpPass) ? "❌ חסר" : "✓ קיים")}");
        Console.WriteLine($"נמען: {SUPPORT_EMAIL}");
        Console.WriteLine($"תלמידה: {student.FirstName} {student.LastName} ({student.StudentID})");
        Console.WriteLine("=====================================");

        // בדיקה אם כל ההגדרות קיימות
        if (string.IsNullOrEmpty(smtpHost))
        {
            Console.WriteLine("❌ שגיאה: SMTP Host חסר ב-appsettings.json!");
            return;
        }

        if (string.IsNullOrEmpty(smtpUser))
        {
            Console.WriteLine("❌ שגיאה: SMTP User חסר ב-appsettings.json!");
            return;
        }

        if (string.IsNullOrEmpty(smtpPass))
        {
            Console.WriteLine("❌ שגיאה: SMTP Password חסר ב-appsettings.json!");
            Console.WriteLine("   לחשבון Google Workspace צריך App Password (16 תווים)");
            return;
        }

        // המרת פורט
        int smtpPort = 587;
        if (!string.IsNullOrEmpty(smtpPortStr))
        {
            if (!int.TryParse(smtpPortStr, out smtpPort))
            {
                Console.WriteLine($"⚠️ אזהרה: פורט לא תקין '{smtpPortStr}', משתמש ב-587");
                smtpPort = 587;
            }
        }

        Console.WriteLine($"📧 מכין מייל עבור {student.FirstName} {student.LastName}...");

        // יצירת תוכן ה-HTML
        string htmlBody = GenerateCertificateHtml(student, completedCourses);

        // יצירת המייל
        using (var message = new MailMessage())
        {
            message.From = new MailAddress(smtpUser, "מערכת הפורטל - בית המורה");
            message.To.Add(SUPPORT_EMAIL);
            message.Subject = $"אישור סיום חובות - {student.FirstName} {student.LastName} ({student.StudentID})";
            message.Body = htmlBody;
            message.IsBodyHtml = true;
            message.Priority = MailPriority.High;

            Console.WriteLine($"📤 מתחבר לשרת SMTP: {smtpHost}:{smtpPort}");

            // שליחת המייל
            using (var client = new SmtpClient(smtpHost, smtpPort))
            {
                client.EnableSsl = true;
                client.Credentials = new NetworkCredential(smtpUser, smtpPass);
                client.Timeout = 30000; // 30 שניות
                client.DeliveryMethod = SmtpDeliveryMethod.Network;

                Console.WriteLine("📨 שולח מייל...");
                await client.SendMailAsync(message);
                Console.WriteLine("✅ הצלחה! המייל נשלח בהצלחה!");
                Console.WriteLine($"   מאת: {smtpUser}");
                Console.WriteLine($"   אל: {SUPPORT_EMAIL}");
                Console.WriteLine($"   נושא: {message.Subject}");
            }
        }
    }
    catch (SmtpException smtpEx)
    {
        // שגיאת SMTP ספציפית
        Console.WriteLine("❌ שגיאת SMTP בשליחת המייל:");
        Console.WriteLine($"   הודעה: {smtpEx.Message}");
        Console.WriteLine($"   קוד סטטוס: {smtpEx.StatusCode}");
        
        // עזרה לפתרון בעיות נפוצות
        if (smtpEx.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            smtpEx.Message.Contains("Username and Password not accepted", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("   💡 עצה: בעיית אימות - ודאי שהסיסמה היא App Password תקין");
            Console.WriteLine("   עבור Google Workspace: צריך App Password בן 16 תווים ללא רווחים");
        }
        else if (smtpEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("   💡 עצה: בעיית זמן תגובה - בדקי חיבור לאינטרנט והגדרות פיירוול");
        }
        
        Console.WriteLine($"   Stack Trace: {smtpEx.StackTrace}");
    }
    catch (Exception ex)
    {
        // שגיאה כללית
        Console.WriteLine("❌ שגיאה כללית בשליחת המייל:");
        Console.WriteLine($"   סוג: {ex.GetType().Name}");
        Console.WriteLine($"   הודעה: {ex.Message}");
        Console.WriteLine($"   Stack Trace: {ex.StackTrace}");
    }
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
    if (val == null || val == DBNull.Value) return false;
    
    // אם זה כבר בוליאני - פשוט להחזיר אותו
    if (val is bool b) return b;
    
    // אם זה מספר (1 או 0)
    if (val is int i) return i == 1;
    
    // אם זו מחרוזת
    string s = val.ToString()?.ToLower()?.Trim() ?? "";
    return s == "true" || s == "1" || s == "yes";
}
        private int GetLessonPrice(string lessonType, string lessonName = "")
{
    if (string.IsNullOrEmpty(lessonType)) return 50;
    if (lessonType.Contains("עבודה מעשית"))
        return (lessonName ?? "").Contains("שנה ג") ? 250 : 600;
    return 50;
}
    }
}
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
using iTextSharp.text;
using iTextSharp.text.html.simpleparser;
using iTextSharp.text.pdf;

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
        private const string ADDITIONAL_EMAIL_1 = "gila.y@byta.org.il";
        private const string ADDITIONAL_EMAIL_2 = "admin@byta.org.il";

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
        // קורסים עם הוראות בלבד, דופליקטים (סומנו כ-!IsAllowedSubmission בFilterDebtsLogic), או שעברו מכסה לא מעכבים את קבלת הדו"ח
        return isAllowed && !isExempt && !isInstructionsOnly && !isFinished;
    });

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
            
            // בדוק אם הקורס עברו מכסה או הוא כפול (IsAllowedSubmission = false)
            // קורסים עם IsAllowedSubmission = false לא מעכבים את קבלת הדו"ח
            bool isAllowedSubmission = !r.ContainsKey("IsAllowedSubmission") || IsTrue(r["IsAllowedSubmission"]);
            
            // אם הקורס עברו מכסה או כפול - לא מעכב
            if (!isAllowedSubmission)
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

    // ✅ POST-PROCESSING: קורסים עם אותו MaterialLink - רק הראשון פתוח להגשה, השאר פטורים מהגשה (לא מתשלום!)
    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in resultList)
    {
        var itemRow = (Dictionary<string, object?>)item;
        string linkVal = itemRow.ContainsKey("MaterialLink") ? (itemRow["MaterialLink"]?.ToString() ?? "").Trim() : "";
        bool isLinkUrl = linkVal.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                         linkVal.StartsWith("www", StringComparison.OrdinalIgnoreCase);

        // רק URL אמיתי, ורק אם הקורס כבר מאושר להגשה (לא נגע בקורסים שכבר false)
        if (!isLinkUrl || string.IsNullOrEmpty(linkVal)) continue;
        if (!IsTrue(itemRow.ContainsKey("IsAllowedSubmission") ? itemRow["IsAllowedSubmission"] : null)) continue;

        if (!seenUrls.Add(linkVal))
        {
            // קישור כפול - פטור מהגשה בלבד, תשלום נשאר כרגיל!
            itemRow["IsAllowedSubmission"] = false;
        }
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
    await SendCompletionCertificateEmailAsync(student, completedDebts); 

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

        private byte[] GenerateCompletionCertificatePdf(dynamic student, IEnumerable<dynamic> completedCourses)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    // יצירת דוקומנט PDF
                    var document = new Document(PageSize.A4, 36, 36, 36, 36);
                    var writer = PdfWriter.GetInstance(document, ms);
                    document.Open();

                    // הגדרת פונט לעברית
                    string fontPath = @"C:\Windows\Fonts\arial.ttf";
                    BaseFont baseFont = BaseFont.CreateFont(fontPath, BaseFont.IDENTITY_H, BaseFont.EMBEDDED);
                    Font titleFont = new Font(baseFont, 16, Font.BOLD);
                    Font headerFont = new Font(baseFont, 12, Font.BOLD);
                    Font normalFont = new Font(baseFont, 10);
                    Font smallFont = new Font(baseFont, 9);

                    // כותרת
                    var titleTable = new PdfPTable(1) { WidthPercentage = 100, RunDirection = PdfWriter.RUN_DIRECTION_RTL };
                    var titleCell = new PdfPCell(new Phrase("אישור סיום חובות לימודיים", titleFont))
                    {
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        BackgroundColor = new BaseColor(0, 51, 102),
                        Padding = 10,
                        RunDirection = PdfWriter.RUN_DIRECTION_RTL
                    };
                    titleCell.FixedHeight = 40;
                    titleTable.AddCell(titleCell);
                    document.Add(titleTable);
                    document.Add(new Paragraph("בית המורה - סמינר שצ'רנסקי", headerFont) { Alignment = Element.ALIGN_CENTER });
                    document.Add(new Paragraph("\n"));

                    // פרטי התלמידה
                    var p1 = new Paragraph($"שם התלמידה: {student.FirstName} {student.LastName}", headerFont);
                    p1.Alignment = Element.ALIGN_RIGHT;
                    document.Add(p1);

                    var p2 = new Paragraph($"תעודת זהות: {student.StudentID}", headerFont);
                    p2.Alignment = Element.ALIGN_RIGHT;
                    document.Add(p2);

                    var p3 = new Paragraph($"תאריך: {DateTime.Now:dd/MM/yyyy}", headerFont);
                    p3.Alignment = Element.ALIGN_RIGHT;
                    document.Add(p3);
                    document.Add(new Paragraph("\n"));

                    // כותרת טבלה
                    var p4 = new Paragraph("פירוט סטטוס קורסים:", headerFont);
                    p4.Alignment = Element.ALIGN_RIGHT;
                    document.Add(p4);

                    // טבלה עם הקורסים
                    var table = new PdfPTable(5) { WidthPercentage = 100, RunDirection = PdfWriter.RUN_DIRECTION_RTL };
                    table.SetWidths(new float[] { 1.5f, 1.5f, 2f, 2f, 2f });

                    // כותרות עמודות
                    string[] headers = { "סטטוס", "מרצה", "שם השיעור", "מס' שיעור", "מטרת לימודים" };
                    foreach (var header in headers)
                    {
                        var cell = new PdfPCell(new Phrase(header, headerFont))
                        {
                            BackgroundColor = new BaseColor(240, 240, 240),
                            HorizontalAlignment = Element.ALIGN_CENTER,
                            Padding = 8,
                            RunDirection = PdfWriter.RUN_DIRECTION_RTL
                        };
                        table.AddCell(cell);
                    }

                    foreach (var item in completedCourses)
                    {
                        var row = SafeConvertToDictionary(item);

                        string lessonNum = row.ContainsKey("LessonNumber") ? row["LessonNumber"]?.ToString() ?? "" : "";
                        string studyGoal = row.ContainsKey("StudyGoal") ? row["StudyGoal"]?.ToString() ?? "" : "";
                        string name = row.ContainsKey("LessonName") ? row["LessonName"]?.ToString() ?? "" : "";
                        string lecturer = row.ContainsKey("LecturerName") ? row["LecturerName"]?.ToString() ?? "" : "";

                        bool isFinished = IsTrue(row["IsPaid"]) && IsTrue(row["IsSubmitted"]);
                        bool isExemptManual = row.ContainsKey("IsExempt") && IsTrue(row["IsExempt"]);
                        bool isAllowed = !row.ContainsKey("IsAllowedSubmission") || IsTrue(row["IsAllowedSubmission"]);
                        bool isInstructionsOnly = row.ContainsKey("IsInstructionsOnly") && IsTrue(row["IsInstructionsOnly"]);

                        string statusText = "";
                        if (isInstructionsOnly)
                            statusText = "לא הושלם";
                        else if (isFinished)
                            statusText = "הושלם";
                        else if (isExemptManual || !isAllowed)
                            statusText = "פטור מהגשה";
                        else
                            statusText = "הושלם";

                        table.AddCell(new PdfPCell(new Phrase(statusText, normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                        table.AddCell(new PdfPCell(new Phrase(lecturer, normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                        table.AddCell(new PdfPCell(new Phrase(name, normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                        table.AddCell(new PdfPCell(new Phrase(lessonNum, normalFont)) { HorizontalAlignment = Element.ALIGN_CENTER });
                        table.AddCell(new PdfPCell(new Phrase(studyGoal, normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                    }

                    document.Add(table);
                    document.Add(new Paragraph("\n"));

                    var footer = new Paragraph("מסמך זה מציג את תמונת המצב של החובות הלימודיים נכון לרגע זה.", smallFont);
                    footer.Alignment = Element.ALIGN_RIGHT;
                    document.Add(footer);

                    var timestamp = new Paragraph($"הודעה זו נוצרה ב: {DateTime.Now:dd/MM/yyyy HH:mm:ss}", smallFont);
                    timestamp.Alignment = Element.ALIGN_CENTER;
                    document.Add(timestamp);

                    document.Close();
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ שגיאה ביצירת PDF: {ex.Message}");
                Console.WriteLine($"   Stack Trace: {ex.StackTrace}");
                return new byte[0];
            }
        }

private async Task SendCompletionCertificateEmailAsync(dynamic student, List<dynamic> completedCourses)
        {
            try
            {
                var smtpHost = _config["Smtp:Host"];
                var smtpPortStr = _config["Smtp:Port"];
                var smtpUser = _config["Smtp:User"];
                var smtpPass = _config["Smtp:Pass"];

                // בדיקה אם כל ההגדרות קיימות
                if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(smtpUser) || string.IsNullOrEmpty(smtpPass))
                {
                    Console.WriteLine("❌ שגיאה: הגדרות SMTP חסרות ב-appsettings.json!");
                    System.IO.File.AppendAllText("C:\\temp\\completion_email_log.txt",
                        $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ SMTP settings missing\n");
                    return;
                }

                int smtpPort = 587;
                if (!string.IsNullOrEmpty(smtpPortStr) && !int.TryParse(smtpPortStr, out smtpPort))
                {
                    smtpPort = 587;
                }

                Console.WriteLine($"🔗 מתחבר לשרת SMTP: {smtpHost}:{smtpPort}");

                using var client = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    Timeout = 30000
                };

                // קריאת רשימת נמענים מהקונפיגורציה
                var recipients = _config.GetSection("AdminEmails:Recipients").Get<List<string>>() ?? new List<string>();
                
                // התחזוקה - אם אין במידע, הוסף את ה-default
                if (recipients == null || recipients.Count == 0)
                {
                    recipients = new List<string> { SUPPORT_EMAIL, ADDITIONAL_EMAIL_1, ADDITIONAL_EMAIL_2 };
                }

                // יצירת תוכן HTML
                string htmlBody = GenerateCertificateHtml(student, completedCourses);

                // יצירת PDF
                byte[] pdfBytes = GenerateCompletionCertificatePdf(student, completedCourses);

                Console.WriteLine($"📧 שולח אישור חובות ל-{recipients.Count} נמענים...");

                foreach (var recipient in recipients)
                {
                    try
                    {
                        using var message = new MailMessage
                        {
                            From = new MailAddress(smtpUser, "מערכת הפורטל - בית המורה"),
                            Subject = $"אישור סיום חובות לימודיים - {student.FirstName} {student.LastName} ({student.StudentID})",
                            Body = htmlBody,
                            IsBodyHtml = true,
                            Priority = MailPriority.High
                        };

                        message.To.Add(recipient);

                        // הוסף PDF כ-attachment אם הוא קיים
                        if (pdfBytes != null && pdfBytes.Length > 0)
                        {
                            var attachment = new Attachment(new MemoryStream(pdfBytes), $"Ishur_Sium_Chovot_{student.StudentID}.pdf", "application/pdf");
                            message.Attachments.Add(attachment);
                        }

                        await client.SendMailAsync(message);
                        Console.WriteLine($"✅ אישור חובות נשלח בהצלחה ל: {recipient}");
                        System.IO.File.AppendAllText("C:\\temp\\completion_email_log.txt",
                            $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ Completion certificate sent to {recipient} for {student.StudentID}\n");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ שגיאה בשליחה ל-{recipient}: {ex.Message}");
                        System.IO.File.AppendAllText("C:\\temp\\completion_email_log.txt",
                            $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ Error sending to {recipient}: {ex.Message}\n");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ שגיאה בתהליך שליחת המייל: {ex.Message}");
                Console.WriteLine($"   Stack Trace: {ex.StackTrace}");
                System.IO.File.AppendAllText("C:\\temp\\completion_email_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ FATAL ERROR: {ex.Message}\n{ex.StackTrace}\n");
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
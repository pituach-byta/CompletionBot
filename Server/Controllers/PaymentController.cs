using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using Dapper;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using iTextSharp.text;
using iTextSharp.text.pdf;
using System.Globalization;

namespace CompletionBot.Server.Controllers
{
    public class NedarimCallbackModel
    {
        public string? TransactionId { get; set; }
        public string? Zeout { get; set; }
        public string? Amount { get; set; }
        public string? Param1 { get; set; }
        public string? Param2 { get; set; }
        public string? Status { get; set; }
        
        // ✅ שדות נוספים שנדרים שולחים (מהמייל שקיבלת)
        public string? Shovar { get; set; }
        public string? ClientName { get; set; }
        public string? Confirmation { get; set; }
        public string? LastNum { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PaymentController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly IConfiguration _configuration;

        public PaymentController(DbService dbService, IConfiguration configuration)
        {
            _dbService = dbService;
            _configuration = configuration;
        }

        [HttpGet("test")]
        public IActionResult TestConnection()
        {
            return Ok("השרת מחובר והכל תקין!");
        }

        // ✅ Endpoint חדש ל-dev-bypass - עדכון DB ישירות לצורכי פיתוח
        [HttpPost("dev-mark-paid")]
        public async Task<IActionResult> DevMarkPaid([FromQuery] string studentId, [FromQuery] string debtIds)
        {
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                $"\n{new string('=', 60)}\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 🔧 DEV-MARK-PAID ENDPOINT CALLED\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                $"StudentID: {studentId}, DebtIds: {debtIds}\n");

            if (string.IsNullOrWhiteSpace(studentId))
            {
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ Missing studentId\n");
                return BadRequest(new { error = "Missing or empty studentId" });
            }

            if (string.IsNullOrWhiteSpace(debtIds))
            {
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ Missing debtIds\n");
                return BadRequest(new { error = "Missing or empty debtIds" });
            }

            try
            {
                var debtIdList = debtIds.Split(',')
                                        .Select(id => id.Trim())
                                        .Where(id => !string.IsNullOrEmpty(id))
                                        .ToList();

                if (debtIdList.Count == 0)
                {
                    System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                        $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ No valid debt IDs after parsing\n");
                    return BadRequest(new { error = "No valid debt IDs" });
                }

                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 📋 Parsed DebtIDs: {string.Join(", ", debtIdList)}\n");

                using var connection = _dbService.CreateConnection();

                var sql = @"UPDATE StudentDebts 
                            SET IsPaid = 1, 
                                TransactionId = @TransactionId, 
                                LastUpdated = GETDATE()
                            WHERE StudentID = @StudentId 
                            AND DebtID IN @DebtIds 
                            AND IsPaid = 0";

                var rowsAffected = await connection.ExecuteAsync(sql, new
                {
                    TransactionId = "dev-bypass-" + DateTime.Now.Ticks,
                    StudentId = studentId,
                    DebtIds = debtIdList
                });

                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ Updated {rowsAffected} debts via dev-bypass\n");

                return Ok(new { success = true, rowsAffected = rowsAffected });
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ DEV-BYPASS ERROR: {ex.Message}\n{ex.StackTrace}\n");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("callback")]
        [Consumes("application/json", "application/x-www-form-urlencoded", "text/plain")]
        public IActionResult NedarimWebhook([FromBody] NedarimCallbackModel data) 
        {
            // ✅ לוג מיידי עם IP
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                $"\n{new string('=', 60)}\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 🔔 CALLBACK RECEIVED\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                $"IP: {clientIp}, X-Forwarded-For: {forwardedFor}\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                $"TransactionId: {data?.TransactionId}, Zeout: {data?.Zeout}\n");
            System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                $"Status: {data?.Status}, Param1: {data?.Param1}\n");

            // ✅ עדכון DB ברקע
            _ = Task.Run(async () =>
            {
                try 
                {
                    if (data != null && !string.IsNullOrEmpty(data.TransactionId) && !string.IsNullOrEmpty(data.Zeout))
                    {
                        System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                            $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 🔄 Starting DB update...\n");
                        
                        await UpdateDebtsInDatabase(data);
                        
                        System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                            $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ DB UPDATED SUCCESSFULLY!\n");
                    }
                    else
                    {
                        System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                            $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ⚠️ Missing required data\n");
                    }
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                        $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ ERROR: {ex.Message}\n{ex.StackTrace}\n");
                }
            });

            // ✅ תשובה פשוטה - text/plain בלבד
            Response.Headers["X-Callback-Status"] = "Received";
            return Content("OK", "text/plain", System.Text.Encoding.UTF8);
        }

        private async Task UpdateDebtsInDatabase(NedarimCallbackModel data)
        {
            using var connection = _dbService.CreateConnection();
            
            if (!string.IsNullOrEmpty(data.Param1))
            {
                var debtIdList = data.Param1.Split(',')
                                           .Select(id => id.Trim())
                                           .Where(id => !string.IsNullOrEmpty(id))
                                           .ToList();

                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 📋 Updating debts: {string.Join(", ", debtIdList)}\n");

                var sql = @"UPDATE StudentDebts 
                            SET IsPaid = 1, 
                                TransactionId = @TransactionId, 
                                LastUpdated = GETDATE()
                            WHERE StudentID = @StudentId 
                            AND DebtID IN @DebtIds 
                            AND IsPaid = 0";

                var rowsAffected = await connection.ExecuteAsync(sql, new { 
                    TransactionId = data.TransactionId, 
                    StudentId = data.Zeout,
                    DebtIds = debtIdList
                });
                
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ Updated {rowsAffected} debts\n");
            }
            else
            {
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ⚠️ Param1 empty - using fallback\n");
                
                var sql = @"UPDATE StudentDebts 
                            SET IsPaid = 1, 
                                TransactionId = @TransactionId, 
                                LastUpdated = GETDATE()
                            WHERE StudentID = @StudentId 
                            AND IsPaid = 0 
                            AND IsActive = 1";

                var rowsAffected = await connection.ExecuteAsync(sql, new { 
                    TransactionId = data.TransactionId, 
                    StudentId = data.Zeout 
                });
                
                System.IO.File.AppendAllText("C:\\temp\\nedarim_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ Fallback: Updated {rowsAffected} debts\n");
            }
        }

        // 📧 Endpoint חדש לשליחת אימייל עם קבלה לצוות ההנהלה
        [HttpPost("send-receipt")]
        public async Task<IActionResult> SendReceipt([FromBody] PaymentReceiptRequest request)
        {
            // לוג מיידי של המידע שהתקבל
            System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt",
                $"\n{new string('=', 60)}\n");
            System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt",
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: 📧 SEND-RECEIPT ENDPOINT CALLED\n");
            System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt",
                $"Request: StudentId={request?.StudentId}, FirstName={request?.FirstName}, LastName={request?.LastName}, DebtsCount={request?.Debts?.Count}\n");
            
            // בדיקה אם FirstName או StudentName קיים (לתמיכה בשני הפורמטים)
            var hasName = !string.IsNullOrEmpty(request?.FirstName) || !string.IsNullOrEmpty(request?.StudentName);
            
            if (request == null || !hasName || request.Debts == null || request.Debts.Count == 0)
            {
                System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt",
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ VALIDATION FAILED - hasName={hasName}, Debts={(request?.Debts?.Count ?? 0)}\n");
                return BadRequest("נתונים חסרים: שם התלמיד או רשימת הקורסים");
            }

            try
            {
                // בנייה של גוף ההודעה
                var emailBody = BuildReceiptEmail(request);
                
                // יצירת קובץ PDF
                var pdfBytes = GenerateReceiptPdf(request);
                
                // קבלת כתובות התשלום מ-appsettings
                var recipients = _configuration.GetSection("AdminEmails:Recipients").Get<List<string>>() ?? new List<string>();
                
                if (recipients.Count == 0)
                {
                    return BadRequest("לא הוגדרו כתובות אימייל לצוות ההנהלה");
                }

                // שליחת אימייל לכל כתובת עם PDF
                await SendEmailAsync(recipients, "פרוט התשלום עבור השלמת עבודות - " + request.FirstName + " " + request.LastName + " (" + request.StudentId + ")", emailBody, pdfBytes);
                
                return Ok(new { success = true, message = "הפרוט נשלחה בהצלחה" });
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ❌ ERROR: {ex.Message}\n{ex.StackTrace}\n");
                return StatusCode(500, $"שגיאה בשליחת הפרוט: {ex.Message}");
            }
        }

        private string BuildReceiptEmail(PaymentReceiptRequest request)
        {
            // שמירת ערכים של התלמיד מראש כדי שישתמשו ב-string interpolation
            var firstName = request?.FirstName ?? "";
            var lastName = request?.LastName ?? "";
            var studentId = request?.StudentId ?? "";
            
            var html = $@"
<!DOCTYPE html>
<html dir='rtl' lang='he'>
<head>
    <meta charset='UTF-8'>
    <style>
        body {{ font-family: Arial, sans-serif; background-color: #f5f5f5; }}
        .container {{ max-width: 600px; margin: 20px auto; background-color: white; border-radius: 8px; padding: 20px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .header {{ background-color: #008f78; color: white; padding: 15px; text-align: center; border-radius: 5px; margin-bottom: 20px; }}
        .header h2 {{ margin: 0; }}
        .student-info {{ background-color: #f9f9f9; padding: 12px; border-radius: 5px; margin: 15px 0; border-right: 4px solid #008f78; }}
        table {{ width: 100%; border-collapse: collapse; margin: 15px 0; }}
        table th {{ background-color: #f0f0f0; padding: 10px; text-align: right; border-bottom: 2px solid #ddd; font-weight: bold; }}
        table td {{ padding: 10px; border-bottom: 1px solid #ddd; }}
        .footer {{ text-align: center; color: #666; font-size: 12px; margin-top: 20px; padding-top: 10px; border-top: 1px solid #ddd; }}
        .total {{ background-color: #fff3cd; padding: 10px; border-radius: 5px; font-weight: bold; text-align: center; }}
        .pdf-note {{ background-color: #d4edda; border: 1px solid #c3e6cb; color: #155724; padding: 10px; border-radius: 5px; margin-top: 15px; font-size: 12px; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h2>פרוט תשלום עבור השלמת עבודות</h2>
        </div>
        
        <div class='student-info'>
            <p style='margin: 5px 0;'><strong>שם התלמידה/ה:</strong> {firstName} {lastName}</p>
            <p style='margin: 5px 0;'><strong>מספר זהות:</strong> {studentId}</p>
        </div>
        
        <p><strong>פרטי הקורסים להם בוצע תשלום:</strong></p>
        <table>
            <thead>
                <tr>
                    <th>שם שיעור</th>
                    <th>סוג שיעור</th>
                    <th>מספר שיעור</th>
                    <th>עלות</th>
                </tr>
            </thead>
            <tbody>";

            var totalAmount = 0;
            if (request?.Debts != null)
            {
                foreach (var debt in request.Debts)
                {
                    var price = debt.Price ?? 50;
                    var lessonNumber = debt.LessonNumber ?? 0;
                    totalAmount += price;
                    html += $@"
                <tr>
                    <td>{debt.LessonName ?? "לא מוגדר"}</td>
                    <td>{debt.LessonType ?? "לא מוגדר"}</td>
                    <td>{lessonNumber}</td>
                    <td>{price} ₪</td>
                </tr>";
                }
            }

            html += $@"
            </tbody>
        </table>
        
        <div class='total'>
            סה""כ לתשלום: {totalAmount} ₪
        </div>
        
        <div class='pdf-note'>
            <strong>📎 קובץ PDF:</strong> קובץ קבלה בפורמט PDF המכיל את כל הפרטים מצורף להודעה זו ומוכן להורדה.
        </div>
        
        <p style='margin-top: 20px; color: #666;'>
            <strong>הערה:</strong> בקבלה זו מופיעה רשימת הקורסים שעבורם בוצע התשלום. 
            אנא עדכני את מערכת התשלומים בהתאם.
        </p>
        
        <div class='footer'>
            <p>הודעה זו נשלחה באופן אוטומטי על ידי מערכת השלמות</p>
            <p>{DateTime.Now:dd/MM/yyyy HH:mm:ss}</p>
        </div>
    </div>
</body>
</html>";

            return html;
        }

        private byte[] GenerateReceiptPdf(PaymentReceiptRequest request)
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
                var titleCell = new PdfPCell(new Phrase("פרוט תשלום עבור השלמת עבודות", titleFont)) 
                { 
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    BackgroundColor = new BaseColor(0, 143, 120),
                    Padding = 10,
                    RunDirection = PdfWriter.RUN_DIRECTION_RTL
                };
                titleCell.FixedHeight = 40;
                titleTable.AddCell(titleCell);
                document.Add(titleTable);
                document.Add(new Paragraph("\n"));

                // פרטי התלמיד
                var p1 = new Paragraph($"שם התלמידה/ה: {request?.FirstName ?? ""} {request?.LastName ?? ""}", headerFont);
                p1.Alignment = Element.ALIGN_RIGHT;
                document.Add(p1);
                
                var p2 = new Paragraph($"מספר זהות: {request?.StudentId ?? ""}", headerFont);
                p2.Alignment = Element.ALIGN_RIGHT;
                document.Add(p2);
                document.Add(new Paragraph("\n"));

                // כותרת טבלה
                var p3 = new Paragraph("פרטי הקורסים להם בוצע תשלום:", headerFont);
                p3.Alignment = Element.ALIGN_RIGHT;
                document.Add(p3);

                // טבלה עם הקורסים - עם RTL
                var table = new PdfPTable(4) { WidthPercentage = 100, RunDirection = PdfWriter.RUN_DIRECTION_RTL };
                table.SetWidths(new float[] { 1.5f, 2, 2, 2 }); // הפוך את הסדר להתאים ל-RTL

                // כותרות עמודות (בסדר הפוך ל-RTL)
                string[] headers = { "שם שיעור", "סוג שיעור", "מספר שיעור", "עלות" };
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

                var totalAmount = 0;
                if (request?.Debts != null)
                {
                    foreach (var debt in request.Debts)
                    {
                        var price = debt.Price ?? 50;
                        var lessonNumber = debt.LessonNumber ?? 0;
                        totalAmount += price;

                        // הוסף בסדר נכון ל-RTL
                        table.AddCell(new PdfPCell(new Phrase(debt.LessonName ?? "לא מוגדר", normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                        table.AddCell(new PdfPCell(new Phrase(debt.LessonType ?? "לא מוגדר", normalFont)) { HorizontalAlignment = Element.ALIGN_RIGHT, RunDirection = PdfWriter.RUN_DIRECTION_RTL });
                        table.AddCell(new PdfPCell(new Phrase(lessonNumber.ToString(), normalFont)) { HorizontalAlignment = Element.ALIGN_CENTER });
                        table.AddCell(new PdfPCell(new Phrase($"{price} ₪", normalFont)) { HorizontalAlignment = Element.ALIGN_CENTER });
                    }
                }

                document.Add(table);
                document.Add(new Paragraph("\n"));

                // סה"כ
                var totalTable = new PdfPTable(1) { WidthPercentage = 100, RunDirection = PdfWriter.RUN_DIRECTION_RTL };
                var totalCell = new PdfPCell(new Phrase($"סה\"כ לתשלום: {totalAmount} ₪", titleFont))
                {
                    BackgroundColor = new BaseColor(255, 243, 205),
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    Padding = 10,
                    RunDirection = PdfWriter.RUN_DIRECTION_RTL
                };
                totalTable.AddCell(totalCell);
                document.Add(totalTable);
                document.Add(new Paragraph("\n"));

                // הערה
                var p4 = new Paragraph("הערה: במסמך זה מופיע רשימת הקורסים שעבורם בוצע התשלום.", smallFont);
                p4.Alignment = Element.ALIGN_RIGHT;
                document.Add(p4);
                document.Add(new Paragraph("\n"));

                // תאריך וזמן
                var footer = new Paragraph($"הודעה זו נוצרה ב: {DateTime.Now:dd/MM/yyyy HH:mm:ss}", smallFont);
                footer.Alignment = Element.ALIGN_CENTER;
                document.Add(footer);

                document.Close();
                return ms.ToArray();
            }
        }

        private string FixHebrewTextForPdf(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            // use StringInfo to properly handle Hebrew text with diacritics
            var stringInfo = new System.Globalization.StringInfo(text);
            var elements = new List<string>();
            
            for (int i = 0; i < stringInfo.LengthInTextElements; i++)
            {
                elements.Add(stringInfo.SubstringByTextElements(i, 1));
            }
            
            elements.Reverse();
            return string.Concat(elements);
        }

        private async Task SendEmailAsync(List<string> recipients, string subject, string htmlBody, byte[]? pdfBytes = null)
        {
            var smtpHost = _configuration["Smtp:Host"];
            var smtpPort = int.Parse(_configuration["Smtp:Port"] ?? "587");
            var smtpUser = _configuration["Smtp:User"];
            var smtpPass = _configuration["Smtp:Pass"];

            using var client = new SmtpClient(smtpHost ?? "smtp.gmail.com", smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(smtpUser, smtpPass)
            };

            foreach (var recipient in recipients)
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(smtpUser ?? ""),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true
                };

                message.To.Add(recipient);

                // הוספת PDF כ-attachment אם הוא קיים
                if (pdfBytes != null && pdfBytes.Length > 0)
                {
                    var attachment = new Attachment(new MemoryStream(pdfBytes), "Receipt.pdf", "application/pdf");
                    message.Attachments.Add(attachment);
                }

                await client.SendMailAsync(message);
                
                System.IO.File.AppendAllText("C:\\temp\\payment_email_log.txt", 
                    $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}: ✅ Email sent to {recipient}\n");
            }
        }
    }

    // 📧 Model לקבלה
    public class PaymentReceiptRequest
    {
        public string? StudentId { get; set; }
        public string? StudentName { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public decimal Amount { get; set; }
        public List<DebtDetail>? Debts { get; set; }
    }

    public class DebtDetail
    {
        public string? LessonName { get; set; }
        public string? LessonType { get; set; }
        public int? Price { get; set; }
        public int? LessonNumber { get; set; }
    }
}
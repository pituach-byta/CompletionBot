using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using Dapper;
using System.Text.Json;

namespace CompletionBot.Server.Controllers
{
    // מודל לקבלת הנתונים מנדרים פלוס (לפי התיעוד ששלחת)
    public class NedarimCallbackModel
    {
        public string? TransactionId { get; set; }
        public string? Zeout { get; set; }
        public string? Amount { get; set; }
        public string? MosadNumber { get; set; }
        public string? Confirmation { get; set; }
        // שדות נוספים מהתיעוד שאפשר להוסיף לפי הצורך
    }

    // מודל לאימות ידני מהלקוח (לסביבת פיתוח)
    public class ClientVerifyRequest
    {
        public string TransactionId { get; set; } = "";
        public string StudentId { get; set; } = "";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PaymentController : ControllerBase
    {
        private readonly DbService _dbService;

        public PaymentController(DbService dbService)
        {
            _dbService = dbService;
        }

        // 1. אימות תשלום המגיע מהלקוח (React) מיד אחרי שהאייפרם הצליח
        // זה קריטי לסביבת פיתוח שבה אין גישה לשרת מבחוץ
        [HttpPost("verify")]
        public async Task<IActionResult> VerifyPayment([FromBody] ClientVerifyRequest request)
        {
            if (string.IsNullOrEmpty(request.TransactionId) || string.IsNullOrEmpty(request.StudentId))
                return BadRequest("נתונים חסרים");

            try
            {
                using var connection = _dbService.CreateConnection();
                
                // עדכון כל החובות הפתוחים של התלמידה לסטטוס "שולם"
                var sql = @"UPDATE StudentDebts 
                            SET IsPaid = 1, TransactionId = @TransactionId, LastUpdated = GETDATE()
                            WHERE StudentID = @StudentId AND IsPaid = 0 AND IsActive = 1";

                await connection.ExecuteAsync(sql, new { 
                    TransactionId = request.TransactionId, 
                    StudentId = request.StudentId 
                });

                return Ok(new { message = "התשלום עודכן בהצלחה במערכת" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בעדכון מסד הנתונים: " + ex.Message);
            }
        }

        // 2. קבלת Webhook רשמי מנדרים פלוס (לכשזה יעלה לשרת אמיתי)
        [HttpPost("callback")]
        public async Task<IActionResult> NedarimWebhook([FromForm] IFormCollection form) 
        {
            // הערה: נדרים שולחים לפעמים Form ולפעמים JSON. כאן נתמוך ב-Form כברירת מחדל
            // אם הם שולחים JSON, יש לשנות את החתימה ל-[FromBody] NedarimCallbackModel model
            
            try 
            {
                var transactionId = form["TransactionId"].ToString();
                var zeout = form["Zeout"].ToString();
                
                if (string.IsNullOrEmpty(transactionId)) return Ok("No TransactionId");

                using var connection = _dbService.CreateConnection();
                var sql = @"UPDATE StudentDebts 
                            SET IsPaid = 1, TransactionId = @TransactionId, LastUpdated = GETDATE()
                            WHERE StudentID = @StudentId AND IsPaid = 0 AND IsActive = 1";

                await connection.ExecuteAsync(sql, new { TransactionId = transactionId, StudentId = zeout });

                return Ok("OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Webhook Error: " + ex.Message);
                return StatusCode(500, "Error");
            }
        }
    }
}
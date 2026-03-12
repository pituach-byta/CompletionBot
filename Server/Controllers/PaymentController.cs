using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using Dapper;

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

        public PaymentController(DbService dbService)
        {
            _dbService = dbService;
        }

        [HttpGet("test")]
        public IActionResult TestConnection()
        {
            return Ok("השרת מחובר והכל תקין!");
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
            Response.Headers.Add("X-Callback-Status", "Received");
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
    }
}
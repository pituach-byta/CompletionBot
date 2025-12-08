using System.Data;
using Microsoft.Data.SqlClient;
using Dapper;
using CompletionBot.Server.Models;

namespace CompletionBot.Server.Services
{
    public class DbService
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public DbService(IConfiguration configuration)
        {
            _configuration = configuration;
            // הוספנו בדיקה שאם אין מחרוזת חיבור - המערכת תזרוק שגיאה ברורה במקום אזהרה
            _connectionString = _configuration.GetConnectionString("DefaultConnection") 
                                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        public async Task<Student?> GetStudentByIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM Students WHERE StudentID = @StudentID";
            return await connection.QuerySingleOrDefaultAsync<Student>(sql, new { StudentID = studentId });
        }

        public async Task<IEnumerable<StudentDebt>> GetDebtsByStudentIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM StudentDebts WHERE StudentID = @StudentID AND IsActive = 1";
            return await connection.QueryAsync<StudentDebt>(sql, new { StudentID = studentId });
        }

        public async Task MarkDebtAsPaidAsync(int debtId, string transactionId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsPaid = 1, TransactionId = @TransactionId, LastUpdated = GETDATE() 
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId, TransactionId = transactionId });
        }
        
         public async Task MarkDebtAsSubmittedAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsSubmitted = 1, LastUpdated = GETDATE() 
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId });
        }
    }
}
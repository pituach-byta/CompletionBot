using System.Data;
using Microsoft.Data.SqlClient;
using Dapper;
using CompletionBot.Server.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CompletionBot.Server.Services
{
    public class DbService
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public DbService(IConfiguration configuration)
        {
            _configuration = configuration;
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

        // --- הפונקציה שהייתה חסרה וגרמה לשגיאה ---
        public async Task<IEnumerable<StudentDebt>> GetDebtsByStudentIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM StudentDebts 
                WHERE StudentID = @StudentID AND IsActive = 1";
            return await connection.QueryAsync<StudentDebt>(sql, new { StudentID = studentId });
        }

        public async Task<IEnumerable<dynamic>> GetStudentDebtsFullDetailsAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    s.StudentID, s.FirstName, s.LastName, s.YearGroup, s.StudentGroup,
                    sd.DebtID, sd.LessonName, sd.LessonType, sd.LessonNumber, 
                    sd.StudyGoal, sd.LecturerName, sd.Hours, sd.MaterialLink, 
                    sd.IsPaid, sd.IsSubmitted, sd.IsExempt,
                    COALESCE(sub.UploadDate, sd.LastUpdated) AS ActualSubmissionDate,
                    sub.FileName, sub.FilePath
                FROM StudentDebts sd
                INNER JOIN Students s ON sd.StudentID = s.StudentID
                LEFT JOIN Submissions sub ON sd.DebtID = sub.DebtID
                WHERE s.StudentID = @StudentID AND sd.IsActive = 1"; 

            return await connection.QueryAsync<dynamic>(sql, new { StudentID = studentId });
        }

        public async Task MarkDebtAsPaidAsync(int debtId, string transactionId = "NedarimPlus")
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

        public async Task<StudentDebt?> GetDebtByIdAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM StudentDebts WHERE DebtID = @DebtID";
            return await connection.QuerySingleOrDefaultAsync<StudentDebt>(sql, new { DebtID = debtId });
        }

        public async Task SaveSubmissionAsync(int debtId, string studentId, string filePath)
        {
            using var connection = CreateConnection();
            var checkSql = "SELECT COUNT(*) FROM Submissions WHERE DebtID = @DebtID";
            int exists = await connection.ExecuteScalarAsync<int>(checkSql, new { DebtID = debtId });

            if (exists > 0)
            {
                var updateSql = @"UPDATE Submissions 
                                  SET FilePath = @FilePath, UploadDate = GETDATE(), FileName = 'Updated File' 
                                  WHERE DebtID = @DebtID";
                await connection.ExecuteAsync(updateSql, new { DebtID = debtId, FilePath = filePath });
            }
            else
            {
                var insertSql = @"INSERT INTO Submissions (DebtID, StudentID, FilePath, UploadDate, FileName) 
                                  VALUES (@DebtID, @StudentID, @FilePath, GETDATE(), 'New Submission')";
                await connection.ExecuteAsync(insertSql, new { DebtID = debtId, StudentID = studentId, FilePath = filePath });
            }

            await MarkDebtAsSubmittedAsync(debtId);
        }
        public async Task UpdateStudentStatusAsync(string studentId, string newStatus)
{
    using var connection = CreateConnection();
    var sql = "UPDATE Students SET Status = @Status WHERE StudentID = @StudentID";
    await connection.ExecuteAsync(sql, new { Status = newStatus, StudentID = studentId });
}

// פונקציה לבדיקה האם התלמידה סיימה את כל חובותיה
public async Task<bool> CheckIfAllDebtsCompletedAsync(string studentId)
{
    using var connection = CreateConnection();
    // בודק אם יש חוב פעיל שהוא לא שולם או לא הוגש או לא פטור
    var sql = @"SELECT COUNT(*) FROM StudentDebts 
                WHERE StudentID = @StudentID 
                AND IsActive = 1 
                AND (IsPaid = 0 OR IsSubmitted = 0) 
                AND IsExempt = 0";
    int remainingDebts = await connection.ExecuteScalarAsync<int>(sql, new { StudentID = studentId });
    return remainingDebts == 0;
}
    }
}

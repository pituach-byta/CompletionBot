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

                    -- בדיקת תאריך הגשה (חדש או ישן)
                    COALESCE(sub.UploadDate, sd.LastUpdated) AS ActualSubmissionDate,

                    -- פרטי הקובץ (אם קיים בחדש)
                    sub.FileName, sub.FilePath

                FROM StudentDebts sd
                INNER JOIN Students s ON sd.StudentID = s.StudentID
                LEFT JOIN Submissions sub ON sd.DebtID = sub.DebtID
                WHERE s.StudentID = @StudentID AND sd.IsActive = 1"; 

            return await connection.QueryAsync<dynamic>(sql, new { StudentID = studentId });
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

        public async Task<StudentDebt?> GetDebtByIdAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM StudentDebts WHERE DebtID = @DebtID";
            return await connection.QuerySingleOrDefaultAsync<StudentDebt>(sql, new { DebtID = debtId });
        }

        // --- הפונקציה המתוקנת והמותאמת לטבלה שלך ---
        public async Task SaveSubmissionAsync(int debtId, string studentId, string filePath)
        {
            using var connection = CreateConnection();
            
            // 1. בדיקה אם כבר יש שורה בטבלת Submissions
            var checkSql = "SELECT COUNT(*) FROM Submissions WHERE DebtID = @DebtID";
            int exists = await connection.ExecuteScalarAsync<int>(checkSql, new { DebtID = debtId });

            if (exists > 0)
            {
                // עדכון (כולל עדכון ה-FileName וה-UploadDate)
                var updateSql = @"UPDATE Submissions 
                                  SET FilePath = @FilePath, 
                                      UploadDate = GETDATE(),
                                      FileName = 'Updated File' 
                                  WHERE DebtID = @DebtID";
                await connection.ExecuteAsync(updateSql, new { DebtID = debtId, FilePath = filePath });
            }
            else
            {
                // יצירה חדשה (כולל StudentID ו-SubmissionID אוטומטי)
                var insertSql = @"INSERT INTO Submissions (DebtID, StudentID, FilePath, UploadDate, FileName) 
                                  VALUES (@DebtID, @StudentID, @FilePath, GETDATE(), 'New Submission')";
                await connection.ExecuteAsync(insertSql, new { DebtID = debtId, StudentID = studentId, FilePath = filePath });
            }

            // 2. עדכון בטבלה הישנה גם כן (בשביל תאימות לאחור)
            await MarkDebtAsSubmittedAsync(debtId);
        }
    }
}
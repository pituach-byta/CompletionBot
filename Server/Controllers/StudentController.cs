using Microsoft.AspNetCore.Mvc;
using CompletionBot.Server.Services;
using CompletionBot.Server.Models;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StudentController : ControllerBase
    {
        private readonly DbService _dbService;

        // כאן אנחנו מקבלים את השירות באופן אוטומטי (Dependency Injection)
        public StudentController(DbService dbService)
        {
            _dbService = dbService;
        }

        // 1. נקודת קצה להזדהות (בדיקה אם התלמידה קיימת)
        // GET: api/student/login/123456789
        [HttpGet("login/{studentId}")]
        public async Task<IActionResult> Login(string studentId)
        {
            var student = await _dbService.GetStudentByIdAsync(studentId);
            
            if (student == null)
            {
                return NotFound(new { message = "תלמידה לא נמצאה במערכת" });
            }

            return Ok(student);
        }

        // 2. נקודת קצה לקבלת רשימת החובות (עבור הטבלה)
        // GET: api/student/debts/123456789
        [HttpGet("debts/{studentId}")]
        public async Task<IActionResult> GetDebts(string studentId)
        {
            var debts = await _dbService.GetDebtsByStudentIdAsync(studentId);
            return Ok(debts);
        }
    }
}
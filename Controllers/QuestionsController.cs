using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using QuizApi.Models;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QuestionsController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public QuestionsController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllQuestions()
        {
            try
            {
                var questions = await _context.QuestionBank
                    .Where(q => q.IsActive == true) 
                    .AsNoTracking()
                    .ToListAsync();

                return Ok(questions);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy danh sách câu hỏi.", error = ex.Message });
            }
        }

        [HttpGet("categories")]
        public async Task<IActionResult> GetCategories()
        {
            try
            {
                var categories = await _context.QuestionBank
                    .Where(q => !string.IsNullOrEmpty(q.Category))
                    .Select(q => q.Category)
                    .Distinct()
                    .ToListAsync();

                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi lấy danh sách danh mục.", error = ex.Message });
            }
        }

        [HttpGet("random")]
        public async Task<IActionResult> GetRandomQuestions([FromQuery] string category, [FromQuery] string level)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(level))
                {
                    return BadRequest(new { message = "Yêu cầu cung cấp đầy đủ 'category' và 'level'." });
                }

                var randomQuestions = await _context.QuestionBank
                    .Where(q => q.Category == category && q.Level == level && q.IsActive == true)
                    .OrderBy(q => Guid.NewGuid())
                    .Take(10)
                    .AsNoTracking()
                    .ToListAsync();

                if (!randomQuestions.Any())
                {
                    return NotFound(new { message = $"Không tìm thấy câu hỏi phù hợp cho môn {category} ở mức độ {level}." });
                }

                return Ok(randomQuestions);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi tạo bộ câu hỏi ngẫu nhiên.", error = ex.Message });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateQuestion([FromBody] QuestionBank newQuestion)
        {
            try
            {
                if (newQuestion == null)
                {
                    return BadRequest(new { message = "Dữ liệu câu hỏi không hợp lệ." });
                }

                newQuestion.CreatedAt = DateTime.Now;
                await _context.QuestionBank.AddAsync(newQuestion);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Thêm mới câu hỏi thành công.", data = newQuestion });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR CREATE QUESTION: " + ex.ToString());
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi thêm mới câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }
        [HttpPut("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateQuestion(int id, [FromBody] QuestionBank updatedQuestion)
        {
            try
            {
                if (id != updatedQuestion.QuestionId)
                {
                    return BadRequest(new { message = "ID trên URL và ID trong body không khớp." });
                }

                var existingQuestion = await _context.QuestionBank.FindAsync(id);
                if (existingQuestion == null)
                {
                    return NotFound(new { message = "Không tìm thấy câu hỏi để cập nhật." });
                }

                existingQuestion.Category = updatedQuestion.Category;
                existingQuestion.Level = updatedQuestion.Level;
                existingQuestion.Content = updatedQuestion.Content;
                existingQuestion.OptionA = updatedQuestion.OptionA;
                existingQuestion.OptionB = updatedQuestion.OptionB;
                existingQuestion.OptionC = updatedQuestion.OptionC;
                existingQuestion.OptionD = updatedQuestion.OptionD;
                existingQuestion.CorrectOption = updatedQuestion.CorrectOption;
                existingQuestion.Explanation = updatedQuestion.Explanation;
                existingQuestion.ScorePerQuestion = updatedQuestion.ScorePerQuestion;
                existingQuestion.IsActive = updatedQuestion.IsActive;

                _context.QuestionBank.Update(existingQuestion);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Cập nhật câu hỏi thành công.", data = existingQuestion });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR UPDATE QUESTION: " + ex.ToString());
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi cập nhật câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteQuestion(int id)
        {
            try
            {
                var question = await _context.QuestionBank.FindAsync(id);
                if (question == null)
                {
                    return NotFound(new { message = "Không tìm thấy câu hỏi để xóa." });
                }

                _context.QuestionBank.Remove(question);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Xóa câu hỏi thành công." });
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi xóa câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }
    }
}

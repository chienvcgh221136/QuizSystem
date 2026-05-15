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
    public class ExamsController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public ExamsController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllExams()
        {
            try
            {
                bool isAdmin = User.IsInRole("Admin");
                var query = _context.Exams.AsNoTracking();

                if (!isAdmin)
                    query = query.Where(e => e.Status == "Published");

                var exams = await query.ToListAsync();
                return Ok(exams);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy danh sách đề thi.", error = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetExamById(int id)
        {
            try
            {
                var exam = await _context.Exams.FindAsync(id);
                if (exam == null)
                {
                    return NotFound(new { message = "Không tìm thấy đề thi." });
                }
                return Ok(exam);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy thông tin đề thi.", error = ex.Message });
            }
        }

        [HttpGet("{id}/full")]
        [AllowAnonymous]
        public async Task<IActionResult> GetFullExam(int id)
        {
            try
            {
                var exam = await _context.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.ExamId == id);
                if (exam == null)
                {
                    return NotFound(new { message = "Không tìm thấy đề thi." });
                }

                var questions = await (from eq in _context.ExamQuestions
                                       join q in _context.QuestionBank on eq.QuestionId equals q.QuestionId
                                       where eq.ExamId == id
                                       orderby eq.OrderIndex
                                       select new
                                       {
                                           q.QuestionId,
                                           q.Content,
                                           q.OptionA,
                                           q.OptionB,
                                           q.OptionC,
                                           q.OptionD,
                                           q.ScorePerQuestion,
                                           eq.OrderIndex
                                       }).ToListAsync();

                return Ok(new
                {
                    ExamInfo = exam,
                    TotalQuestions = questions.Count,
                    Questions = questions
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy đề thi đầy đủ câu hỏi.", error = ex.Message });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateExam([FromBody] Exam newExam)
        {
            try
            {
                if (newExam == null)
                {
                    return BadRequest(new { message = "Dữ liệu đề thi không hợp lệ." });
                }
                
                newExam.CreatedAt = DateTime.Now;

                await _context.Exams.AddAsync(newExam);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetExamById), new { id = newExam.ExamId }, newExam);
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi tạo đề thi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateExam(int id, [FromBody] Exam updatedExam)
        {
            try
            {
                if (id != updatedExam.ExamId)
                {
                    return BadRequest(new { message = "ID không khớp." });
                }

                var existingExam = await _context.Exams.FindAsync(id);
                if (existingExam == null)
                {
                    return NotFound(new { message = "Không tìm thấy đề thi." });
                }

                existingExam.Title = updatedExam.Title;
                existingExam.Description = updatedExam.Description;
                existingExam.Category = updatedExam.Category;
                existingExam.Level = updatedExam.Level;
                existingExam.TimeLimit = updatedExam.TimeLimit;
                existingExam.TotalScore = updatedExam.TotalScore;
                existingExam.Status = updatedExam.Status;
                existingExam.ApprovedBy = updatedExam.ApprovedBy;
                existingExam.ApprovedAt = updatedExam.ApprovedAt;

                _context.Exams.Update(existingExam);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Cập nhật đề thi thành công.", data = existingExam });
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi cập nhật đề thi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteExam(int id)
        {
            try
            {
                var exam = await _context.Exams.FindAsync(id);
                if (exam == null)
                {
                    return NotFound(new { message = "Không tìm thấy đề thi để xóa." });
                }

                var examQuestions = await _context.ExamQuestions.Where(eq => eq.ExamId == id).ToListAsync();
                if (examQuestions.Any())
                {
                    _context.ExamQuestions.RemoveRange(examQuestions);
                }

                _context.Exams.Remove(exam);
                
                await _context.SaveChangesAsync();

                return Ok(new { message = "Xóa đề thi và các liên kết thành công." });
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi xóa đề thi.", error = ex.Message, innerError = innerMsg });
            }
        }
    }
}

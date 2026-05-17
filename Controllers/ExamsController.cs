using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using QuizApi.Models;

namespace QuizApi.Controllers
{
    public class CreateExamRequest : Exam
    {
        public int? QuestionCount { get; set; }
    }

    public class FullQuestionRequest
    {
        public string Text { get; set; } = string.Empty;
        public string Type { get; set; } = "Multiple Choice";
        public List<string> Options { get; set; } = new();
        public string Answer { get; set; } = string.Empty;
    }

    public class CreateFullExamRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Level { get; set; } = "Trung cấp";
        public int TimeLimit { get; set; } = 30;
        public double TotalScore { get; set; } = 10;
        public string Status { get; set; } = "Draft";
        public List<FullQuestionRequest> Questions { get; set; } = new();
    }

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
                var query = _context.Exams.AsNoTracking();
                
                // Nếu không phải Admin, chỉ trả về đề thi có status là "Published"
                if (!User.IsInRole("Admin"))
                {
                    query = query.Where(e => e.Status == "Published");
                }
                
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

                // Nếu đề thi chưa công bố và người dùng không phải Admin thì chặn
                if (exam.Status != "Published" && !User.IsInRole("Admin"))
                {
                    return BadRequest(new { message = "Đề thi này chưa được công bố." });
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

                // Nếu đề thi chưa công bố và người dùng không phải Admin thì chặn
                bool isAdmin = User.Identity?.IsAuthenticated == true && User.IsInRole("Admin");
                if (exam.Status != "Published" && !isAdmin)
                {
                    return BadRequest(new { message = "Đề thi này chưa được công bố." });
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
        public async Task<IActionResult> CreateExam([FromBody] CreateExamRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { message = "Dữ liệu đề thi không hợp lệ." });
                
                var newExam = new Exam
                {
                    Title = request.Title,
                    Description = request.Description,
                    Category = request.Category,
                    Level = request.Level,
                    TimeLimit = request.TimeLimit,
                    TotalScore = request.TotalScore,
                    Status = request.Status ?? "Draft",
                    CreatedBy = User.Identity?.Name ?? User.Claims.FirstOrDefault(c => c.Type == "sub" || c.Type.Contains("name"))?.Value ?? "admin",
                    CreatedAt = DateTime.Now
                };

                await _context.Exams.AddAsync(newExam);
                await _context.SaveChangesAsync();

                // Tự động gán câu hỏi nếu có QuestionCount
                if (request.QuestionCount.HasValue && request.QuestionCount.Value > 0)
                {
                    var randomQuestions = await _context.QuestionBank
                        .Where(q => q.Category == newExam.Category && q.Level == newExam.Level)
                        .OrderBy(r => Guid.NewGuid())
                        .Take(request.QuestionCount.Value)
                        .ToListAsync();

                    for (int i = 0; i < randomQuestions.Count; i++)
                    {
                        await _context.ExamQuestions.AddAsync(new ExamQuestion
                        {
                            ExamId = newExam.ExamId,
                            QuestionId = randomQuestions[i].QuestionId,
                            OrderIndex = i + 1
                        });
                    }
                    await _context.SaveChangesAsync();
                }

                return CreatedAtAction(nameof(GetExamById), new { id = newExam.ExamId }, newExam);
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi tạo đề thi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpPost("{id}/questions/{questionId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AddQuestion(int id, int questionId)
        {
            try
            {
                var exists = await _context.ExamQuestions.AnyAsync(eq => eq.ExamId == id && eq.QuestionId == questionId);
                if (exists) return BadRequest(new { message = "Câu hỏi đã có trong đề thi." });

                var maxOrder = await _context.ExamQuestions
                    .Where(eq => eq.ExamId == id)
                    .MaxAsync(eq => (int?)eq.OrderIndex) ?? 0;

                var link = new ExamQuestion
                {
                    ExamId = id,
                    QuestionId = questionId,
                    OrderIndex = maxOrder + 1
                };

                await _context.ExamQuestions.AddAsync(link);
                await _context.SaveChangesAsync();
                return Ok(new { message = "Đã thêm câu hỏi vào đề." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi thêm câu hỏi.", error = ex.Message });
            }
        }

        [HttpDelete("{id}/questions/{questionId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RemoveQuestion(int id, int questionId)
        {
            try
            {
                var link = await _context.ExamQuestions.FirstOrDefaultAsync(eq => eq.ExamId == id && eq.QuestionId == questionId);
                if (link == null) return NotFound(new { message = "Không tìm thấy liên kết." });

                _context.ExamQuestions.Remove(link);
                await _context.SaveChangesAsync();
                return Ok(new { message = "Đã xóa câu hỏi khỏi đề." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi xóa câu hỏi.", error = ex.Message });
            }
        }

        [HttpGet("{id}/available-questions")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAvailableQuestions(int id)
        {
            try
            {
                var exam = await _context.Exams.FindAsync(id);
                if (exam == null) return NotFound();

                var currentQuestionIds = await _context.ExamQuestions
                    .Where(eq => eq.ExamId == id)
                    .Select(eq => eq.QuestionId)
                    .ToListAsync();

                var available = await _context.QuestionBank
                    .Where(q => q.Category == exam.Category && !currentQuestionIds.Contains(q.QuestionId))
                    .ToListAsync();

                return Ok(available);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi lấy danh sách câu hỏi.", error = ex.Message });
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

        [HttpPost("full")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateFullExam([FromBody] CreateFullExamRequest request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var newExam = new Exam
                {
                    Title = request.Title,
                    Category = request.Category,
                    Level = request.Level,
                    TimeLimit = request.TimeLimit,
                    TotalScore = request.TotalScore,
                    Status = request.Status,
                    CreatedBy = User.Identity?.Name ?? "admin",
                    CreatedAt = DateTime.Now
                };

                await _context.Exams.AddAsync(newExam);
                await _context.SaveChangesAsync();

                int order = 1;
                foreach (var qReq in request.Questions)
                {
                    // Sanitize Answer: Ensure it is "A", "B", "C", or "D"
                    string sanitizedAnswer = qReq.Answer?.Trim().ToUpper() ?? "A";
                    if (sanitizedAnswer.Length > 1) {
                        // AI returned full text, try to find which option it matches
                        if (qReq.Options.Count > 0 && qReq.Answer == qReq.Options[0]) sanitizedAnswer = "A";
                        else if (qReq.Options.Count > 1 && qReq.Answer == qReq.Options[1]) sanitizedAnswer = "B";
                        else if (qReq.Options.Count > 2 && qReq.Answer == qReq.Options[2]) sanitizedAnswer = "C";
                        else if (qReq.Options.Count > 3 && qReq.Answer == qReq.Options[3]) sanitizedAnswer = "D";
                        else sanitizedAnswer = "A"; // Default fallback
                    }

                    var newQuestion = new QuestionBank
                    {
                        Content = qReq.Text,
                        Category = request.Category,
                        Level = request.Level,
                        OptionA = qReq.Options.Count > 0 ? qReq.Options[0] : "",
                        OptionB = qReq.Options.Count > 1 ? qReq.Options[1] : "",
                        OptionC = qReq.Options.Count > 2 ? qReq.Options[2] : "",
                        OptionD = qReq.Options.Count > 3 ? qReq.Options[3] : "",
                        CorrectOption = sanitizedAnswer,
                        ScorePerQuestion = (double)request.TotalScore / request.Questions.Count,
                        CreatedAt = DateTime.Now
                    };

                    await _context.QuestionBank.AddAsync(newQuestion);
                    await _context.SaveChangesAsync();

                    await _context.ExamQuestions.AddAsync(new ExamQuestion
                    {
                        ExamId = newExam.ExamId,
                        QuestionId = newQuestion.QuestionId,
                        OrderIndex = order++
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { message = "Đã lưu bộ đề thi thành công!", examId = newExam.ExamId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                Console.WriteLine($"[CreateFullExam Error] {ex.Message}. Inner: {innerMsg}");
                return StatusCode(500, new { message = "Lỗi khi lưu bộ đề đầy đủ.", error = ex.Message, innerError = innerMsg });
            }
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExamResultsController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public ExamResultsController(QuizDbContext context)
        {
            _context = context;
        }

        // ============================================================
        // BƯỚC 1: User bắt đầu làm bài thi
        // POST /api/ExamResults/start
        // ============================================================
        [HttpPost("start")]
        public async Task<IActionResult> StartExam([FromBody] StartExamRequest request)
        {
            try
            {
                // Lấy UserId từ JWT Token
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return Unauthorized();
                int userId = int.Parse(userIdClaim);

                // Kiểm tra đề thi tồn tại và đang Published
                var exam = await _context.Exams.FirstOrDefaultAsync(e => e.ExamId == request.ExamId && e.Status == "Published");
                if (exam == null)
                    return NotFound(new { message = "Không tìm thấy đề thi hoặc đề chưa được công bố." });

                // Kiểm tra user đã bắt đầu làm đề này chưa (chưa nộp)
                var existing = await _context.ExamResults.FirstOrDefaultAsync(
                    r => r.ExamId == request.ExamId && r.UserId == userId && r.Status == "InProgress");
                if (existing != null)
                    return Ok(new { message = "Bạn đang làm dở bài này.", resultId = existing.ResultId });

                // Tạo bản ghi kết quả mới
                var result = new ExamResult
                {
                    ExamId = request.ExamId,
                    UserId = userId,
                    StartTime = DateTime.Now,
                    Status = "InProgress"
                };

                _context.ExamResults.Add(result);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = $"Bắt đầu làm bài '{exam.Title}'. Chúc bạn làm bài tốt!",
                    resultId = result.ResultId,
                    examId = exam.ExamId,
                    timeLimit = exam.TimeLimit
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống.", error = ex.Message });
            }
        }

        // ============================================================
        // BƯỚC 2: User nộp bài + Chấm điểm tự động
        // POST /api/ExamResults/{resultId}/submit
        // ============================================================
        [HttpPost("{resultId}/submit")]
        public async Task<IActionResult> SubmitExam(int resultId, [FromBody] SubmitExamRequest request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return Unauthorized();
                int userId = int.Parse(userIdClaim);

                // Kiểm tra bản ghi kết quả
                var result = await _context.ExamResults.FirstOrDefaultAsync(
                    r => r.ResultId == resultId && r.UserId == userId && r.Status == "InProgress");
                if (result == null)
                    return NotFound(new { message = "Không tìm thấy bài thi đang làm." });

                double totalScore = 0;
                var userAnswers = new List<UserAnswer>();

                // Chấm từng câu trả lời
                foreach (var answer in request.Answers)
                {
                    var question = await _context.QuestionBank.FindAsync(answer.QuestionId);
                    if (question == null) continue;

                    bool isCorrect = question.CorrectOption?.ToUpper() == answer.SelectedOption?.ToUpper();
                    if (isCorrect) totalScore += question.ScorePerQuestion;

                    userAnswers.Add(new UserAnswer
                    {
                        ResultId = resultId,
                        QuestionId = answer.QuestionId,
                        SelectedOption = answer.SelectedOption,
                        IsCorrect = isCorrect,
                        AnsweredAt = DateTime.Now
                    });
                }

                // Lưu tất cả câu trả lời
                _context.UserAnswers.AddRange(userAnswers);

                // Cập nhật kết quả
                result.Score = Math.Round(totalScore, 2);
                result.SubmitTime = DateTime.Now;
                result.Status = "Submitted";

                await _context.SaveChangesAsync();

                // Lấy tổng điểm tối đa của đề thi
                var exam = await _context.Exams.FindAsync(result.ExamId);

                return Ok(new
                {
                    message = "Nộp bài thành công!",
                    resultId = result.ResultId,
                    score = result.Score,
                    maxScore = exam?.TotalScore,
                    totalAnswered = userAnswers.Count,
                    correctCount = userAnswers.Count(a => a.IsCorrect == true),
                    wrongCount = userAnswers.Count(a => a.IsCorrect == false),
                    timeTaken = $"{(result.SubmitTime - result.StartTime)?.Minutes} phút"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống.", error = ex.Message });
            }
        }

        // ============================================================
        // BƯỚC 3: User xem kết quả chi tiết
        // GET /api/ExamResults/{resultId}
        // ============================================================
        [HttpGet("{resultId}")]
        public async Task<IActionResult> GetResult(int resultId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return Unauthorized();
                int userId = int.Parse(userIdClaim);

                var result = await _context.ExamResults.FirstOrDefaultAsync(
                    r => r.ResultId == resultId && r.UserId == userId);
                if (result == null)
                    return NotFound(new { message = "Không tìm thấy kết quả bài thi." });

                var exam = await _context.Exams.FindAsync(result.ExamId);

                // Lấy câu trả lời kèm đáp án đúng để review
                var answers = await (from ua in _context.UserAnswers
                                     join q in _context.QuestionBank on ua.QuestionId equals q.QuestionId
                                     where ua.ResultId == resultId
                                     orderby ua.AnswerId
                                     select new
                                     {
                                         ua.QuestionId,
                                         q.Content,
                                         q.OptionA,
                                         q.OptionB,
                                         q.OptionC,
                                         q.OptionD,
                                         ua.SelectedOption,
                                         CorrectOption = q.CorrectOption, // Hiện đáp án đúng khi xem lại
                                         ua.IsCorrect,
                                         q.ScorePerQuestion
                                     }).ToListAsync();

                return Ok(new
                {
                    ResultInfo = new
                    {
                        result.ResultId,
                        ExamTitle = exam?.Title,
                        result.Score,
                        MaxScore = exam?.TotalScore,
                        result.StartTime,
                        result.SubmitTime,
                        result.Status
                    },
                    Summary = new
                    {
                        TotalQuestions = answers.Count,
                        Correct = answers.Count(a => a.IsCorrect == true),
                        Wrong = answers.Count(a => a.IsCorrect == false)
                    },
                    Answers = answers
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống.", error = ex.Message });
            }
        }

        // Lấy lịch sử làm bài của user
        [HttpGet("my-history")]
        public async Task<IActionResult> GetMyHistory()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null) return Unauthorized();
            int userId = int.Parse(userIdClaim);

            var history = await (from r in _context.ExamResults
                                 join e in _context.Exams on r.ExamId equals e.ExamId
                                 where r.UserId == userId
                                 orderby r.StartTime descending
                                 select new
                                 {
                                     r.ResultId,
                                     e.Title,
                                     e.Category,
                                     r.Score,
                                     MaxScore = e.TotalScore,
                                     r.StartTime,
                                     r.SubmitTime,
                                     r.Status
                                 }).ToListAsync();

            return Ok(history);
        }
    }

    // ===== Request Models =====
    public class StartExamRequest
    {
        public int ExamId { get; set; }
    }

    public class SubmitExamRequest
    {
        public List<AnswerItem> Answers { get; set; } = new();
    }

    public class AnswerItem
    {
        public int QuestionId { get; set; }
        public string? SelectedOption { get; set; }
    }
}

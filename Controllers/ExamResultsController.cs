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

        [HttpPost("start")]
        public async Task<IActionResult> StartExam([FromBody] StartExamRequest request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return Unauthorized();
                int userId = int.Parse(userIdClaim);
                var exam = await _context.Exams.FindAsync(request.ExamId);
                if (exam == null)
                    return NotFound(new { message = "Không tìm thấy đề thi." });
                bool isAdmin = User.IsInRole("Admin");
                if (!isAdmin && exam.Status != "Published")
                    return BadRequest(new { message = "Đề thi này chưa được công bố." });
                var existing = await _context.ExamResults.FirstOrDefaultAsync(
                    r => r.ExamId == request.ExamId && r.UserId == userId && r.Status == "In_Progress");
                if (existing != null)
                    return Ok(new { message = "Bạn đang làm dở bài này.", resultId = existing.ResultId });

                var result = new ExamResult
                {
                    ExamId = request.ExamId,
                    UserId = userId,
                    StartTime = DateTime.Now,
                    Status = "In_Progress"
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

        [HttpPost("{resultId}/submit")]
        public async Task<IActionResult> SubmitExam(int resultId, [FromBody] SubmitExamRequest request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim == null) return Unauthorized();
                int userId = int.Parse(userIdClaim);

                var result = await _context.ExamResults.FirstOrDefaultAsync(
                    r => r.ResultId == resultId && r.UserId == userId && r.Status == "In_Progress");
                if (result == null)
                    return NotFound(new { message = "Không tìm thấy bài thi đang làm." });

                // 1. Lấy danh sách ID các câu hỏi thực sự thuộc về đề thi này
                var examQuestions = await _context.ExamQuestions
                    .Where(eq => eq.ExamId == result.ExamId)
                    .Select(eq => eq.QuestionId)
                    .ToListAsync();

                double totalScore = 0;
                var userAnswers = new List<UserAnswer>();

                // 2. Chỉ xử lý các câu trả lời nằm trong danh sách câu hỏi của đề
                foreach (var questionId in examQuestions)
                {
                    var answer = request.Answers.FirstOrDefault(a => a.QuestionId == questionId);
                    var question = await _context.QuestionBank.FindAsync(questionId);
                    
                    if (question == null) continue;

                    bool isCorrect = false;
                    string? selectedOption = null;

                    if (answer != null)
                    {
                        selectedOption = answer.SelectedOption;
                        isCorrect = question.CorrectOption?.ToUpper() == selectedOption?.ToUpper();
                        if (isCorrect) totalScore += question.ScorePerQuestion;
                    }

                    userAnswers.Add(new UserAnswer
                    {
                        ResultId = resultId,
                        QuestionId = questionId,
                        SelectedOption = selectedOption,
                        IsCorrect = isCorrect,
                        AnsweredAt = DateTime.Now
                    });
                }

                _context.UserAnswers.AddRange(userAnswers);

                result.Score = Math.Round(totalScore, 2);
                result.SubmitTime = DateTime.Now;
                result.Status = "Submitted";

                await _context.SaveChangesAsync();

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
                                         CorrectOption = q.CorrectOption,
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
                                     e.ExamId,
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

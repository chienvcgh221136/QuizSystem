using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using QuizApi.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Chỉ Admin/User đã đăng nhập mới dùng được chatbot
    public class ChatbotController : ControllerBase
    {
        private readonly GeminiService _geminiService;
        private readonly QuizDbContext _context;

        public ChatbotController(GeminiService geminiService, QuizDbContext context)
        {
            _geminiService = geminiService;
            _context = context;
        }

        // Endpoint debug: Xem tất cả model Gemini được hỗ trợ
        [HttpGet("list-models")]
        [AllowAnonymous]
        public async Task<IActionResult> ListModels()
        {
            var result = await _geminiService.ListModelsAsync();
            return Ok(result);
        }

        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] ChatRequest request)
        {
            if (string.IsNullOrEmpty(request.Message))
                return BadRequest("Tin nhắn không được để trống.");

            string userMsg = request.Message.ToLower();

            // 1. Phân tích lệnh tạo đề thi bằng Regex (KHÔNG tốn quota Gemini)
            if (userMsg.Contains("tạo đề") || userMsg.Contains("tạo bài thi") || userMsg.Contains("tạo bài kiểm tra"))
            {
                var (category, count) = ParseExamIntentLocally(request.Message);
                if (!string.IsNullOrEmpty(category) && count > 0)
                {
                    return await HandleCreateExam(category, count);
                }
                return Ok(new { response = "Vui lòng nói rõ chủ đề và số lượng câu hỏi. Ví dụ: 'Tạo đề thi C# 20 câu'" });
            }

            // 2. Hỏi đáp thông thường -> Gọi Gemini
            try
            {
                var aiResponse = await _geminiService.ChatAsync(request.Message);
                return Ok(new { response = aiResponse });
            }
            catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("TooManyRequests"))
            {
                return Ok(new { response = "Xin lỗi, trợ lý AI đang bận. Tuy nhiên tôi vẫn có thể tạo đề thi cho bạn! Hãy thử: 'Tạo đề thi C# 10 câu'" });
            }
        }

        // Phân tích ý định tạo đề thi bằng Regex (không tốn quota)
        private (string Category, int Count) ParseExamIntentLocally(string message)
        {
            // Tìm số lượng câu hỏi (ví dụ: "20 câu", "30 câu")
            var countMatch = System.Text.RegularExpressions.Regex.Match(message, @"(\d+)\s*câu");
            int count = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 10;

            // Tìm category (tìm các keyword phổ biến)
            string category = "";
            var categoryMap = new Dictionary<string, string[]>
            {
                { "C#",         new[] { "c#", "csharp", "c sharp" } },
                { "IT",         new[] { "it", "công nghệ thông tin", "cntt" } },
                { "SQL Server", new[] { "sql", "sql server", "database" } },
                { "ASP.NET",    new[] { "asp.net", "aspnet", "asp net" } },
                { "Math",       new[] { "toán", "math", "toán học" } },
            };

            string msgLower = message.ToLower();
            foreach (var kv in categoryMap)
            {
                if (kv.Value.Any(k => msgLower.Contains(k)))
                {
                    category = kv.Key;
                    break;
                }
            }

            return (category, count);
        }

        private async Task<IActionResult> HandleCreateExam(string category, int count)
        {
            // Tìm các câu hỏi trong QuestionBank theo category
            var availableQuestions = await _context.QuestionBank
                .Where(q => q.Category == category && q.IsActive == true)
                .ToListAsync();

            if (availableQuestions.Count == 0)
            {
                return Ok(new { response = $"Tôi xin lỗi, hiện tại trong kho không có câu hỏi nào thuộc chủ đề '{category}'." });
            }

            int actualCount = Math.Min(count, availableQuestions.Count);

            // Xáo trộn và lấy ngẫu nhiên
            var randomQuestions = availableQuestions
                .OrderBy(q => Guid.NewGuid())
                .Take(actualCount)
                .ToList();

            // Tạo đề thi mới (Trạng thái Draft)
            double totalScore = Math.Round(randomQuestions.Sum(q => q.ScorePerQuestion), 2);
            var newExam = new Exam
            {
                Title = $"Đề thi {category} (Tạo bởi AI)",
                Description = $"Đề thi tự động gồm {actualCount} câu hỏi chủ đề {category}.",
                Category = category,
                Level = "Trung cấp",
                TimeLimit = actualCount * 2, // Mặc định 2 phút/câu
                TotalScore = totalScore,      // Tính tổng điểm từ các câu hỏi
                Status = "Published",         // DB chỉ chấp nhận: Published, Draft, Archived
                CreatedAt = DateTime.Now,
                CreatedBy = "Admin"
            };

            _context.Exams.Add(newExam);
            await _context.SaveChangesAsync();

            // Thêm các câu hỏi vào bảng trung gian
            for (int i = 0; i < randomQuestions.Count; i++)
            {
                var eq = new ExamQuestion
                {
                    ExamId = newExam.ExamId,
                    QuestionId = randomQuestions[i].QuestionId,
                    OrderIndex = i + 1
                };
                _context.ExamQuestions.Add(eq);
            }

            await _context.SaveChangesAsync();

            return Ok(new { 
                response = $"Đã tạo xong đề thi '{newExam.Title}' với {actualCount} câu hỏi. Bạn có thể vào phần Quản lý đề thi để chỉnh sửa và đăng lên.",
                examId = newExam.ExamId,
                intent = "create_exam"
            });
        }
    }

    public class ChatRequest
    {
        public string? Message { get; set; }
    }
}

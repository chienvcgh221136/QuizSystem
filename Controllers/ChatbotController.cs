using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using QuizApi.Services;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] 
    public class ChatbotController : ControllerBase
    {
        private readonly GroqService _groqService;
        private readonly QuizDbContext _context;

        public ChatbotController(GroqService groqService, QuizDbContext context)
        {
            _groqService = groqService;
            _context = context;
        }



        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] ChatRequest request)
        {
            if (string.IsNullOrEmpty(request.Message))
                return BadRequest("Tin nhắn không được để trống.");

            string userMsg = request.Message.ToLower();

            var stats = await _context.QuestionBank
                .GroupBy(q => q.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();
            string dbStats = string.Join(", ", stats.Select(s => $"{s.Category}: {s.Count} câu"));

            string currentUsername = User.Identity?.Name ?? "Admin";
            var history = await _context.ChatMessages
                .Where(m => m.Username == currentUsername)
                .OrderByDescending(m => m.SentAt)
                .Take(10)
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            string historyContext = string.Join("\n", history.Select(h => $"User: {h.UserMessage}\nAI: {h.AiResponse}"));

            string systemPrompt = $@"You are the 'QuizChat Local AI Tutor'.
CURRENT DATABASE STATS: {dbStats}

STRICT RULES:
1. You are an expert in the categories listed above. 
2. If the user asks for an exam, you SHOULD use existing question counts as a reference, but you ARE encouraged to generate NEW high-quality questions to fulfill the request.
3. You CANNOT provide information from the external world (news, etc.).
4. CONTEXT MEMORY: Use the history below to stay on track.
5. ALWAYS respond with valid JSON. Do not include any text outside the JSON.
6. EXAM JSON RULE: For each question, the ""answer"" field MUST be exactly one character: ""A"", ""B"", ""C"", or ""D"". DO NOT put the full text of the answer in the ""answer"" field.

CONVERSATION HISTORY:
{historyContext}

JSON OUTPUT FORMAT:
If creating an exam:
{{
  ""intent"": ""create_exam"",
  ""title"": ""Exam Title"",
  ""category"": ""Category"",
  ""timeLimit"": 30,
  ""totalScore"": 10,
  ""questions"": [
    {{ ""text"": ""Question?"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""A"" }}
  ],
  ""message"": ""Confirmation message.""
}}
If chatting:
{{ ""message"": ""Your response."" }}";

            try
            {
                var aiRawResponse = await _groqService.ChatAsync($"{systemPrompt}\n\nUser: {request.Message}");
                string aiText = aiRawResponse;
                bool hasDraft = false;
                string? metadata = null;

                try {
                    int start = aiRawResponse.IndexOf('{');
                    int end = aiRawResponse.LastIndexOf('}');
                    if (start != -1 && end != -1) {
                        var jsonStr = aiRawResponse.Substring(start, end - start + 1);
                        var data = JsonSerializer.Deserialize<JsonElement>(jsonStr);
                        
                        if (data.TryGetProperty("intent", out var intent) && intent.GetString() == "create_exam") {
                            hasDraft = true;
                            aiText = data.GetProperty("message").GetString() ?? "I have created the exam draft for you.";
                            metadata = jsonStr;

                            var chatMsg = new ChatMessage {
                                Username = User.Identity?.Name ?? "Admin",
                                UserMessage = request.Message,
                                AiResponse = aiText,
                                Metadata = metadata
                            };
                            _context.ChatMessages.Add(chatMsg);
                            await _context.SaveChangesAsync();

                            return Ok(new { 
                                intent = "create_exam",
                                hasDraft = hasDraft,
                                message = aiText,
                                title = data.GetProperty("title").GetString(),
                                category = data.GetProperty("category").GetString(),
                                timeLimit = data.GetProperty("timeLimit").GetInt32(),
                                totalScore = data.GetProperty("totalScore").GetDouble(),
                                questions = data.GetProperty("questions")
                            });
                        }
                    }
                } catch { /* Chat thường */ }

                var normalMsg = new ChatMessage {
                    Username = User.Identity?.Name ?? "Admin",
                    UserMessage = request.Message,
                    AiResponse = aiText
                };
                _context.ChatMessages.Add(normalMsg);
                await _context.SaveChangesAsync();

                return Ok(new { message = aiText, hasDraft = false });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chatbot Error] {ex.Message}");
                return Ok(new { message = "Xin lỗi, tôi gặp một chút trục trặc khi kết nối với bộ não AI. Vui lòng thử lại sau giây lát.", hasDraft = false });
            }
        }

        private (string Category, int Count) ParseExamIntentLocally(string message)
        {
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
            var availableQuestions = await _context.QuestionBank
                .Where(q => q.Category == category && q.IsActive == true)
                .ToListAsync();

            if (availableQuestions.Count == 0)
            {
                return Ok(new { response = $"Tôi xin lỗi, hiện tại trong kho không có câu hỏi nào thuộc chủ đề '{category}'." });
            }

            int actualCount = Math.Min(count, availableQuestions.Count);

            var randomQuestions = availableQuestions
                .OrderBy(q => Guid.NewGuid())
                .Take(actualCount)
                .ToList();
            double totalScore = Math.Round(randomQuestions.Sum(q => q.ScorePerQuestion), 2);
            var newExam = new Exam
            {
                Title = $"Đề thi {category} (Tạo bởi AI)",
                Description = $"Đề thi tự động gồm {actualCount} câu hỏi chủ đề {category}.",
                Category = category,
                Level = "Trung cấp",
                TimeLimit = actualCount * 2, 
                TotalScore = totalScore,
                Status = "Published",         
                CreatedAt = DateTime.Now,
                CreatedBy = "Admin"
            };

            _context.Exams.Add(newExam);
            await _context.SaveChangesAsync();

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
                response = $"Certainly! I have generated a {actualCount}-question {category} exam for you. You can see the live draft on the right panel.",
                examId = newExam.ExamId,
                title = newExam.Title,
                category = newExam.Category,
                timeLimit = newExam.TimeLimit,
                totalScore = newExam.TotalScore,
                questions = randomQuestions.Select(q => new {
                    id = q.QuestionId,
                    text = q.Content,
                    type = "Multiple Choice",
                    options = new[] { q.OptionA, q.OptionB, q.OptionC, q.OptionD },
                    answer = q.CorrectOption
                }),
                intent = "create_exam"
            });
        }
    }

    public class ChatRequest
    {
        public string? Message { get; set; }
    }
}

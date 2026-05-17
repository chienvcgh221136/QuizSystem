using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using QuizApi.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        private static string RepairTruncatedJson(string json)
        {
            json = json.Trim();
            if (string.IsNullOrEmpty(json)) return "{}";

            var stack = new List<char>();
            bool inQuote = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (!inQuote)
                {
                    if (c == '{' || c == '[')
                    {
                        stack.Add(c);
                    }
                    else if (c == '}' || c == ']')
                    {
                        if (stack.Count > 0)
                        {
                            char expected = c == '}' ? '{' : '[';
                            if (stack[stack.Count - 1] == expected)
                            {
                                stack.RemoveAt(stack.Count - 1);
                            }
                        }
                    }
                }
            }

            var sb = new StringBuilder(json);
            if (inQuote)
            {
                sb.Append('"');
            }

            while (sb.Length > 0 && char.IsWhiteSpace(sb[sb.Length - 1]))
            {
                sb.Length--;
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ',')
            {
                sb.Length--;
            }

            for (int i = stack.Count - 1; i >= 0; i--)
            {
                char open = stack[i];
                if (open == '{')
                {
                    sb.Append('}');
                }
                else if (open == '[')
                {
                    sb.Append(']');
                }
            }

            return sb.ToString();
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

            string systemPrompt = $@"You are the 'QuizChat Local AI Tutor'.
CURRENT DATABASE STATS: {dbStats}

STRICT RULES:
1. You are an expert in the categories listed above. 
2. If the user asks for an exam, you SHOULD use existing question counts as a reference, but you ARE encouraged to generate NEW high-quality questions to fulfill the request.
3. You CANNOT provide information from the external world (news, etc.).
4. CONTEXT MEMORY: Use the previous conversation history provided in the chat logs to stay on track.
5. ALWAYS respond with valid JSON. Do not include any text outside the JSON.
6. EXAM JSON RULE: For each question, the ""answer"" field MUST be exactly one character: ""A"", ""B"", ""C"", or ""D"". DO NOT put the full text of the answer in the ""answer"" field.
7. EXAM LEVEL RULE: You must determine the difficulty level requested by the user (""Sơ cấp"" for basic/elementary, ""Trung cấp"" for intermediate, ""Cao cấp"" for advanced) and include it in the ""level"" property of the JSON. If not specified, default to ""Trung cấp"".
8. If the user asks to create or generate a quiz or exam (e.g. ""tạo đề"", ""soạn đề"", ""generate exam"", ""create quiz""), you MUST set ""intent"": ""create_exam"" and generate the full questions array immediately. Do not ask clarifying questions first.

JSON OUTPUT FORMAT:
If creating an exam:
{{
  ""intent"": ""create_exam"",
  ""title"": ""Exam Title"",
  ""category"": ""Category"",
  ""level"": ""Sơ cấp"" (or ""Trung cấp"" or ""Cao cấp""),
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
                var aiRawResponse = await _groqService.ChatAsync(systemPrompt, history, request.Message);
                string aiText = aiRawResponse;
                bool hasDraft = false;
                string? metadata = null;

                try {
                    int start = aiRawResponse.IndexOf('{');
                    if (start != -1) {
                        string potentialJson = aiRawResponse.Substring(start);
                        string repairedJson = RepairTruncatedJson(potentialJson);
                        var data = JsonSerializer.Deserialize<JsonElement>(repairedJson);
                        
                        if (data.TryGetProperty("intent", out var intentProp) && intentProp.GetString() == "create_exam") {
                            hasDraft = true;
                            
                            string title = "Đề thi mới";
                            if (data.TryGetProperty("title", out var titleProp)) title = titleProp.GetString() ?? title;

                            string category = "Chung";
                            if (data.TryGetProperty("category", out var catProp)) category = catProp.GetString() ?? category;

                            string level = "Sơ cấp";
                            if (data.TryGetProperty("level", out var levelProp)) {
                                level = levelProp.GetString() ?? level;
                            } else {
                                string lowerMsg = request.Message.ToLower();
                                if (lowerMsg.Contains("sơ cấp") || lowerMsg.Contains("dễ") || lowerMsg.Contains("cơ bản") || lowerMsg.Contains("elementary") || lowerMsg.Contains("easy")) {
                                    level = "Sơ cấp";
                                } else if (lowerMsg.Contains("cao cấp") || lowerMsg.Contains("khó") || lowerMsg.Contains("advanced") || lowerMsg.Contains("hard")) {
                                    level = "Cao cấp";
                                } else {
                                    level = "Trung cấp";
                                }
                            }

                            int timeLimit = 30;
                            if (data.TryGetProperty("timeLimit", out var tlProp)) {
                                if (tlProp.ValueKind == JsonValueKind.Number) {
                                    timeLimit = tlProp.GetInt32();
                                } else if (tlProp.ValueKind == JsonValueKind.String && int.TryParse(tlProp.GetString(), out int tlVal)) {
                                    timeLimit = tlVal;
                                }
                            }

                            double totalScore = 10.0;
                            if (data.TryGetProperty("totalScore", out var tsProp)) {
                                if (tsProp.ValueKind == JsonValueKind.Number) {
                                    totalScore = tsProp.GetDouble();
                                } else if (tsProp.ValueKind == JsonValueKind.String && double.TryParse(tsProp.GetString(), out double tsVal)) {
                                    totalScore = tsVal;
                                }
                            }

                            string message = $"Tôi đã soạn thảo đề thi \"{title}\" ({category} - {level}) thành công. Bạn có thể xem và chỉnh sửa ở khung bên phải.";
                            if (data.TryGetProperty("message", out var msgProp)) {
                                message = msgProp.GetString() ?? message;
                            }

                            var validQuestionsList = new List<object>();
                            if (data.TryGetProperty("questions", out var questionsProp) && questionsProp.ValueKind == JsonValueKind.Array) {
                                foreach (var qElem in questionsProp.EnumerateArray()) {
                                    try {
                                        string qText = qElem.TryGetProperty("text", out var qt) ? qt.GetString() ?? "" : "";
                                        if (string.IsNullOrEmpty(qText)) continue;

                                        string qType = qElem.TryGetProperty("type", out var qtyp) ? qtyp.GetString() ?? "Multiple Choice" : "Multiple Choice";
                                        
                                        var optionsList = new List<string>();
                                        if (qElem.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array) {
                                            foreach (var opt in opts.EnumerateArray()) {
                                                optionsList.Add(opt.GetString() ?? "");
                                            }
                                        }

                                        string qAns = qElem.TryGetProperty("answer", out var qans) ? qans.GetString() ?? "A" : "A";
                                        qAns = qAns.Trim().ToUpper();
                                        if (qAns.Length > 1) {
                                            if (optionsList.Count > 0 && (qAns.Contains(optionsList[0].ToUpper()) || qAns == optionsList[0].ToUpper())) qAns = "A";
                                            else if (optionsList.Count > 1 && (qAns.Contains(optionsList[1].ToUpper()) || qAns == optionsList[1].ToUpper())) qAns = "B";
                                            else if (optionsList.Count > 2 && (qAns.Contains(optionsList[2].ToUpper()) || qAns == optionsList[2].ToUpper())) qAns = "C";
                                            else if (optionsList.Count > 3 && (qAns.Contains(optionsList[3].ToUpper()) || qAns == optionsList[3].ToUpper())) qAns = "D";
                                            else if (qAns.StartsWith("A") || qAns.StartsWith("OPTION A")) qAns = "A";
                                            else if (qAns.StartsWith("B") || qAns.StartsWith("OPTION B")) qAns = "B";
                                            else if (qAns.StartsWith("C") || qAns.StartsWith("OPTION C")) qAns = "C";
                                            else if (qAns.StartsWith("D") || qAns.StartsWith("OPTION D")) qAns = "D";
                                            else qAns = "A";
                                        }

                                        validQuestionsList.Add(new {
                                            text = qText,
                                            type = qType,
                                            options = optionsList,
                                            answer = qAns
                                        });
                                    } catch {
                                        // Skip invalid question element
                                    }
                                }
                            }

                            aiText = message;
                            metadata = JsonSerializer.Serialize(new {
                                intent = "create_exam",
                                title = title,
                                category = category,
                                level = level,
                                timeLimit = timeLimit,
                                totalScore = totalScore,
                                questions = validQuestionsList,
                                message = message
                            });

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
                                title = title,
                                category = category,
                                level = level,
                                timeLimit = timeLimit,
                                totalScore = totalScore,
                                questions = validQuestionsList
                            });
                        }
                        else
                        {
                            if (data.TryGetProperty("message", out var msgProp))
                            {
                                aiText = msgProp.GetString() ?? aiText;
                            }
                        }
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[Chatbot JSON Parse Error] {ex.Message}");
                }

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

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

        private static string TruncateText(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static List<ChatMessage> BuildCompactHistory(List<ChatMessage> history, int maxTurns = 3)
        {
            return history
                .TakeLast(maxTurns)
                .Select(message => new ChatMessage
                {
                    Username = message.Username,
                    UserMessage = TruncateText(message.UserMessage, 400),
                    AiResponse = TruncateText(message.AiResponse, 400),
                    SentAt = message.SentAt,
                    Metadata = null
                })
                .ToList();
        }

        private static string? BuildCompactPreviousExamContext(string? metadata, List<int> requestedQuestionIndexes)
        {
            if (string.IsNullOrWhiteSpace(metadata)) return null;

            try
            {
                using var doc = JsonDocument.Parse(metadata);
                var root = doc.RootElement;

                var questions = new List<object>();
                if (root.TryGetProperty("questions", out var questionsProp) && questionsProp.ValueKind == JsonValueKind.Array)
                {
                    var allQuestions = questionsProp.EnumerateArray().ToList();
                    var indexesToInclude = requestedQuestionIndexes.Count > 0
                        ? requestedQuestionIndexes.Distinct().Where(index => index > 0 && index <= allQuestions.Count).ToList()
                        : Enumerable.Range(1, allQuestions.Count).ToList();

                    foreach (var index in indexesToInclude)
                    {
                        var question = allQuestions[index - 1];
                        questions.Add(new
                        {
                            index,
                            text = question.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty,
                            type = question.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "Multiple Choice" : "Multiple Choice",
                            options = question.TryGetProperty("options", out var optionsProp) && optionsProp.ValueKind == JsonValueKind.Array
                                ? optionsProp.EnumerateArray().Select(option => option.GetString() ?? string.Empty).ToList()
                                : new List<string>()
                        });
                    }
                }

                var compactContext = new
                {
                    title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty,
                    category = root.TryGetProperty("category", out var categoryProp) ? categoryProp.GetString() ?? string.Empty : string.Empty,
                    level = root.TryGetProperty("level", out var levelProp) ? levelProp.GetString() ?? string.Empty : string.Empty,
                    timeLimit = root.TryGetProperty("timeLimit", out var timeLimitProp) && timeLimitProp.ValueKind == JsonValueKind.Number
                        ? timeLimitProp.GetInt32()
                        : 30,
                    totalScore = root.TryGetProperty("totalScore", out var totalScoreProp) && totalScoreProp.ValueKind == JsonValueKind.Number
                        ? totalScoreProp.GetDouble()
                        : 10.0,
                    questionCount = questions.Count,
                    requestedQuestionIndexes,
                    questions
                };

                return JsonSerializer.Serialize(compactContext);
            }
            catch
            {
                return null;
            }
        }

        private static int? ParseRequestedQuestionCount(string message)
        {
            var regex = new System.Text.RegularExpressions.Regex(@"(?:tạo|soạn|generate|create)?\s*(?:đề|quiz|exam)?[^\d]{0,20}(\d+)\s*(?:câu|cau|câu hỏi|question|questions)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var match = regex.Match(message);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count) && count > 0)
            {
                return count;
            }

            return null;
        }

        private static (int? TargetTotal, int? AdditionalCount, bool IsIncreaseRequest) ParseQuestionCountRequest(string message)
        {
            var lower = message.ToLowerInvariant();

            var rangeMatch = System.Text.RegularExpressions.Regex.Match(
                lower,
                @"từ\s+(\d+)\s*(?:câu|cau|câu hỏi|question|questions)?\s*(?:lên|đến|thành|tới)\s+(\d+)\s*(?:câu|cau|câu hỏi|question|questions)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[2].Value, out var rangeTarget) && rangeTarget > 0)
            {
                return (rangeTarget, null, true);
            }

            var additionalMatch = System.Text.RegularExpressions.Regex.Match(
                lower,
                @"(?:thêm|bổ sung|cộng thêm)\s+(\d+)\s*(?:câu|cau|câu hỏi|question|questions)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (additionalMatch.Success && int.TryParse(additionalMatch.Groups[1].Value, out var additionalCount) && additionalCount > 0)
            {
                return (null, additionalCount, true);
            }

            var increaseMatch = System.Text.RegularExpressions.Regex.Match(
                lower,
                @"(?:tăng|nâng|mở rộng)\s+(?:lên|đến|thành)?\s*(\d+)\s*(?:câu|cau|câu hỏi|question|questions)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (increaseMatch.Success && int.TryParse(increaseMatch.Groups[1].Value, out var increaseTarget) && increaseTarget > 0)
            {
                return (increaseTarget, null, true);
            }

            var plainMatch = System.Text.RegularExpressions.Regex.Match(
                lower,
                @"(\d+)\s*(?:câu|cau|câu hỏi|question|questions)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (plainMatch.Success && int.TryParse(plainMatch.Groups[1].Value, out var plainCount) && plainCount > 0)
            {
                return (plainCount, null, false);
            }

            return (null, null, false);
        }

        private async Task<List<Dictionary<string, object>>> BuildRandomQuestionItems(string category, int count)
        {
            var questions = await _context.QuestionBank
                .Where(q => q.Category == category && q.IsActive == true)
                .OrderBy(q => Guid.NewGuid())
                .Take(count)
                .ToListAsync();

            return questions.Select(q => new Dictionary<string, object>
            {
                ["text"] = q.Content,
                ["type"] = "Multiple Choice",
                ["options"] = new[] { q.OptionA, q.OptionB, q.OptionC, q.OptionD }.ToList(),
                ["answer"] = q.CorrectOption
            }).ToList();
        }

        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] ChatRequest request)
        {
            if (string.IsNullOrEmpty(request.Message))
                return BadRequest("Tin nhắn không được để trống.");

            string userMsg = request.Message.ToLower();
            bool isExamRequest = userMsg.Contains("tạo đề") || userMsg.Contains("soạn đề") || userMsg.Contains("generate exam") || userMsg.Contains("create exam") || userMsg.Contains("đề thi") || userMsg.Contains("quiz");
            var requestedQuestionIndexes = ParseRequestedQuestionIndexes(request.Message);
            var requestedQuestionCount = ParseRequestedQuestionCount(request.Message);

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
            var compactHistory = BuildCompactHistory(history, 3);
            bool isExamModificationRequest = userMsg.Contains("thay đổi câu") || userMsg.Contains("đổi câu") || userMsg.Contains("sửa câu") || userMsg.Contains("chỉnh sửa câu") || userMsg.Contains("đổi đề") || userMsg.Contains("thay đổi đề") || userMsg.Contains("sửa đề") || userMsg.Contains("thay đổi độ khó") || userMsg.Contains("giảm độ khó") || userMsg.Contains("tăng độ khó");
            
            // Debug logging
            Console.WriteLine($"[Debug] isExamModificationRequest: {isExamModificationRequest}");
            Console.WriteLine($"[Debug] requestedQuestionIndexes: {string.Join(", ", requestedQuestionIndexes)}");
            Console.WriteLine($"[Debug] User message: {request.Message}");
            
            string? previousExamMetadata = null;
            if (isExamModificationRequest)
            {
                previousExamMetadata = history
                    .Where(m => !string.IsNullOrEmpty(m.Metadata))
                    .OrderByDescending(m => m.SentAt)
                    .Select(m => m.Metadata)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(previousExamMetadata))
                {
                    try
                    {
                        var prevDoc = JsonDocument.Parse(previousExamMetadata);
                        if (!prevDoc.RootElement.TryGetProperty("intent", out var prevIntent) || prevIntent.GetString() != "create_exam")
                        {
                            previousExamMetadata = null;
                        }
                    }
                    catch
                    {
                        previousExamMetadata = null;
                    }
                }
            }
            string? compactPreviousExamContext = BuildCompactPreviousExamContext(previousExamMetadata, requestedQuestionIndexes);
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
8a. If the user specifies a number of questions, generate exactly that many questions. Do not generate more or fewer.
9. PROACTIVE SUGGESTION RULE: Whenever you generate or modify an exam (intent: ""create_exam""), the ""message"" property in your JSON MUST ALWAYS end with a friendly question in Vietnamese asking the user if they want to modify a specific question, change the difficulty, or randomize a completely new set of questions. (Example: ""Bạn có muốn thay đổi câu hỏi nào không, hoặc tạo một đề thi khác?"").
10. NO MARKDOWN: Output ONLY raw JSON text. DO NOT wrap the response in ```json or any markdown formatting.
11. MODIFICATION WITHOUT FULL REGEN: If user only wants to modify 1 or a few specific questions (e.g., ""sửa câu 1"", ""thay đổi câu 2 và 3""), do NOT regenerate the entire exam. Instead, return ONLY the specific modified question(s) with their new content. The backend will handle patching them back into the full exam automatically.

JSON OUTPUT FORMAT:
If creating a NEW exam:
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
If modifying specific question(s) only:
{{
  ""intent"": ""modify_questions"",
  ""modifiedQuestions"": [
    {{ ""index"": 1, ""text"": ""New Q1?"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""B"" }},
    {{ ""index"": 3, ""text"": ""New Q3?"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""C"" }}
  ],
  ""message"": ""I've modified the questions you requested.""
}}
If just chatting:
{{ ""message"": ""Your response."" }}";

                        if (!string.IsNullOrEmpty(compactPreviousExamContext))
                        {
                                systemPrompt += $@"

PREVIOUS_EXAM_CONTEXT: {compactPreviousExamContext}

!!!CRITICAL - READ THIS CAREFULLY!!!
User wants to MODIFY EXISTING QUESTIONS only (not create a new exam).

Your response format MUST be ONE of these:

CASE 1: User says 'sửa câu 1', 'thay đổi câu 2', 'đổi câu hỏi 3', etc.
Return ONLY the modified questions in this format:
{{
    ""intent"": ""modify_questions"",
    ""modifiedQuestions"": [
        {{ ""index"": 1, ""text"": ""New question text for #1"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""B"" }}
    ],
    ""message"": ""Đã sửa câu 1 theo yêu cầu của bạn.""
}}

EXAMPLE: If user says 'sửa câu 1 và 3':
{{
    ""intent"": ""modify_questions"",
    ""modifiedQuestions"": [
        {{ ""index"": 1, ""text"": ""Modified Q1"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""A"" }},
        {{ ""index"": 3, ""text"": ""Modified Q3"", ""type"": ""Multiple Choice"", ""options"": [""A"", ""B"", ""C"", ""D""], ""answer"": ""D"" }}
    ],
    ""message"": ""Đã sửa câu 1 và 3 của bạn.""
}}

IMPORTANT RULES:
- ONLY return the questions user asked to modify
- Include ""index"" field (1-based number: 1, 2, 3, etc.)
- Keep ""answer"" as a SINGLE CHARACTER: A, B, C, or D
- Do NOT include questions the user did NOT ask to modify
- Do NOT regenerate the entire exam
- Use intent ""modify_questions"" ONLY when modifying specific questions

CASE 2: User explicitly asks to INCREASE or ADD questions (e.g., ""tăng lên 40 câu"", ""thêm 10 câu""):
Return intent ""modify_questions"" and include ONLY the new questions in one of these formats:

- Option A: ""additionalQuestions"": [ {{ ""text"": ""New question text"", ""type"": ""Multiple Choice"", ""options"": [...], ""answer"": ""A"" }} ]
- Option B: ""modifiedQuestions"": [ {{ ""index"": 31, ""text"": ""New question text"", ""type"": ""Multiple Choice"", ""options"": [...], ""answer"": ""A"" }} ]

When using Option B, indexes MUST be the 1-based positions where the new questions should be inserted (e.g., if original exam had 30 questions and the user asked to increase to 40, return indexes 31..40).
When using Option A, the backend will append the new questions to the end of the existing exam.

IMPORTANT: Do NOT return the entire previous exam when the user asked to add questions. Return only the new questions or the specific modified ones.";
            }

            try
            {
                var aiRawResponse = await _groqService.ChatAsync(systemPrompt, compactHistory, request.Message);
                string aiText = aiRawResponse;
                bool hasDraft = false;
                string? metadata = null;
                var requestedCount = requestedQuestionCount ?? 0;

                try {
                    string cleanedResponse = aiRawResponse.Replace("```json", "").Replace("```", "").Trim();
                    int start = cleanedResponse.IndexOf('{');
                    if (start != -1) {
                        string potentialJson = cleanedResponse.Substring(start);
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

                            bool questionCountAdjusted = false;
                            int questionCountRequested = requestedCount > 0 ? requestedCount : 0;
                            int questionCountActual = 0;
                            var validQuestionsList = new List<Dictionary<string, object>>();
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

                                        var questionItem = new Dictionary<string, object>
                                        {
                                            ["text"] = qText,
                                            ["type"] = qType,
                                            ["options"] = optionsList,
                                            ["answer"] = qAns
                                        };
                                        if (qElem.TryGetProperty("questionIndex", out var qIndexProp) && qIndexProp.ValueKind == JsonValueKind.Number && qIndexProp.TryGetInt32(out var questionIndex))
                                        {
                                            questionItem["questionIndex"] = questionIndex;
                                        }
                                        validQuestionsList.Add(questionItem);
                                    } catch {
                                        // Skip invalid question element
                                    }
                                }
                            }

                            if (!isExamModificationRequest && requestedCount > 0)
                            {
                                if (validQuestionsList.Count > requestedCount)
                                {
                                    validQuestionsList = validQuestionsList.Take(requestedCount).ToList();
                                    questionCountAdjusted = true;
                                }
                                else if (validQuestionsList.Count < requestedCount)
                                {
                                    return await HandleCreateExam(category, requestedCount, requestedCount);
                                }
                            }

                            questionCountActual = validQuestionsList.Count;
                            if (questionCountRequested > 0 && questionCountActual != questionCountRequested)
                            {
                                questionCountAdjusted = true;
                            }

                            if (isExamModificationRequest && !string.IsNullOrEmpty(previousExamMetadata))
                            {
                                try
                                {
                                    var prevDoc = JsonDocument.Parse(previousExamMetadata);
                                    if (prevDoc.RootElement.TryGetProperty("questions", out var prevQuestionsProp) && prevQuestionsProp.ValueKind == JsonValueKind.Array)
                                    {
                                        var prevQuestions = prevQuestionsProp.EnumerateArray().ToList();

                                        // IMPORTANT: If user requested specific question modifications, patch only those questions
                                        if (requestedQuestionIndexes.Count > 0)
                                        {
                                            // Strategy: The AI should return ALL questions. If it doesn't, we use the ones it did return to patch requested indexes
                                            var patchedQuestions = new List<Dictionary<string, object>>();
                                            
                                            for (int i = 0; i < prevQuestions.Count; i++)
                                            {
                                                int questionNum = i + 1; // 1-based index
                                                bool isRequestedIndex = requestedQuestionIndexes.Contains(questionNum);

                                                if (isRequestedIndex && i < validQuestionsList.Count)
                                                {
                                                    // Use the AI-modified question for this index
                                                    patchedQuestions.Add(validQuestionsList[i]);
                                                }
                                                else
                                                {
                                                    // Preserve the previous question unchanged
                                                    var prev = prevQuestions[i];
                                                    var preservedQuestion = new Dictionary<string, object>
                                                    {
                                                        ["text"] = prev.TryGetProperty("text", out var pt) ? pt.GetString() ?? string.Empty : string.Empty,
                                                        ["type"] = prev.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice",
                                                        ["options"] = prev.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array ? popts.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>(),
                                                        ["answer"] = prev.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A"
                                                    };
                                                    patchedQuestions.Add(preservedQuestion);
                                                }
                                            }
                                            validQuestionsList = patchedQuestions;
                                        }
                                                                else
                                                                {
                                                                    // No specific questions were requested to be modified.
                                                                    // If the AI returned the same number of questions, it's a full regeneration - use it.
                                                                    // If different count, decide if user asked to increase questions.
                                                                    if (validQuestionsList.Count != prevQuestions.Count)
                                                                    {
                                                                        // Inspect user's message to understand increase vs replace
                                                                        var (targetTotal, additionalCount, isIncreaseRequest) = ParseQuestionCountRequest(request.Message);

                                                                        if (isIncreaseRequest)
                                                                        {
                                                                            int desired = targetTotal ?? (prevQuestions.Count + (additionalCount ?? 0));
                                                                            int toAdd = Math.Max(0, desired - prevQuestions.Count);
                                                                            if (toAdd > 0)
                                                                            {
                                                                                var extra = await BuildRandomQuestionItems(category, toAdd);
                                                                                var patched = prevQuestions.Select(prev => new Dictionary<string, object>
                                                                                {
                                                                                    ["text"] = prev.TryGetProperty("text", out var pt) ? pt.GetString() ?? string.Empty : string.Empty,
                                                                                    ["type"] = prev.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice",
                                                                                    ["options"] = prev.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array ? popts.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>(),
                                                                                    ["answer"] = prev.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A"
                                                                                }).ToList();

                                                                                patched.AddRange(extra);
                                                                                validQuestionsList = patched;
                                                                            }
                                                                            else
                                                                            {
                                                                                // Fallback: preserve previous exam
                                                                                validQuestionsList = prevQuestions.Select(prev => new Dictionary<string, object>
                                                                                {
                                                                                    ["text"] = prev.TryGetProperty("text", out var pt) ? pt.GetString() ?? string.Empty : string.Empty,
                                                                                    ["type"] = prev.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice",
                                                                                    ["options"] = prev.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array ? popts.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>(),
                                                                                    ["answer"] = prev.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A"
                                                                                }).ToList();
                                                                            }
                                                                        }
                                                                        else
                                                                        {
                                                                            // Preserve the entire previous exam for safety.
                                                                            validQuestionsList = prevQuestions.Select(prev => new Dictionary<string, object>
                                                                            {
                                                                                ["text"] = prev.TryGetProperty("text", out var pt) ? pt.GetString() ?? string.Empty : string.Empty,
                                                                                ["type"] = prev.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice",
                                                                                ["options"] = prev.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array ? popts.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>(),
                                                                                ["answer"] = prev.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A"
                                                                            }).ToList();
                                                                        }
                                                                    }
                                                                }
                                    }
                                }
                                catch
                                {
                                    // If previous metadata cannot be parsed, fall back to AI response.
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
                                questions = validQuestionsList,
                                questionCountRequested = questionCountRequested,
                                questionCountActual = questionCountActual,
                                questionCountAdjusted = questionCountAdjusted,
                                warning = questionCountAdjusted && questionCountRequested > 0
                                    ? $"Đề đã được điều chỉnh về {questionCountActual} câu hỏi để khớp yêu cầu/nguồn dữ liệu."
                                    : null
                            });
                        }
                        else if (data.TryGetProperty("intent", out var modifyIntentProp) && modifyIntentProp.GetString() == "modify_questions") 
                        {
                            // Handle question modification request
                            Console.WriteLine($"[Debug] Processing modify_questions intent");
                            
                            if (data.TryGetProperty("modifiedQuestions", out var modifiedQsProp) && modifiedQsProp.ValueKind == JsonValueKind.Array)
                            {
                                // Parse the modified questions
                                var modifiedQuestionsDict = new Dictionary<int, Dictionary<string, object>>();
                                
                                foreach (var mqElem in modifiedQsProp.EnumerateArray())
                                {
                                    try
                                    {
                                        if (!mqElem.TryGetProperty("index", out var indexProp) || !indexProp.TryGetInt32(out var idx))
                                            continue;

                                        string qText = mqElem.TryGetProperty("text", out var qt) ? qt.GetString() ?? "" : "";
                                        if (string.IsNullOrEmpty(qText)) continue;

                                        string qType = mqElem.TryGetProperty("type", out var qtyp) ? qtyp.GetString() ?? "Multiple Choice" : "Multiple Choice";
                                        
                                        var optionsList = new List<string>();
                                        if (mqElem.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array) {
                                            foreach (var opt in opts.EnumerateArray()) {
                                                optionsList.Add(opt.GetString() ?? "");
                                            }
                                        }

                                        string qAns = mqElem.TryGetProperty("answer", out var qans) ? qans.GetString() ?? "A" : "A";
                                        qAns = qAns.Trim().ToUpper();
                                        if (qAns.Length > 1) {
                                            if (optionsList.Count > 0 && qAns.Contains(optionsList[0].ToUpper())) qAns = "A";
                                            else if (optionsList.Count > 1 && qAns.Contains(optionsList[1].ToUpper())) qAns = "B";
                                            else if (optionsList.Count > 2 && qAns.Contains(optionsList[2].ToUpper())) qAns = "C";
                                            else if (optionsList.Count > 3 && qAns.Contains(optionsList[3].ToUpper())) qAns = "D";
                                            else qAns = "A";
                                        }

                                        modifiedQuestionsDict[idx] = new Dictionary<string, object>
                                        {
                                            ["text"] = qText,
                                            ["type"] = qType,
                                            ["options"] = optionsList,
                                            ["answer"] = qAns
                                        };
                                    }
                                    catch { }
                                }

                                Console.WriteLine($"[Debug] Modified {modifiedQuestionsDict.Count} questions");

                                    // Check for explicit 'additionalQuestions' provided by AI
                                    var additionalQuestionsList = new List<Dictionary<string, object>>();
                                    if (data.TryGetProperty("additionalQuestions", out var additionalQsProp) && additionalQsProp.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var aq in additionalQsProp.EnumerateArray())
                                        {
                                            try
                                            {
                                                string qText = aq.TryGetProperty("text", out var qt) ? qt.GetString() ?? "" : "";
                                                if (string.IsNullOrEmpty(qText)) continue;
                                                string qType = aq.TryGetProperty("type", out var qtyp) ? qtyp.GetString() ?? "Multiple Choice" : "Multiple Choice";
                                                var optionsList = new List<string>();
                                                if (aq.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                                                {
                                                    foreach (var opt in opts.EnumerateArray()) optionsList.Add(opt.GetString() ?? "");
                                                }
                                                string qAns = aq.TryGetProperty("answer", out var qans) ? qans.GetString() ?? "A" : "A";
                                                qAns = qAns.Trim().ToUpper();
                                                if (qAns.Length > 1) qAns = qAns[0].ToString();

                                                additionalQuestionsList.Add(new Dictionary<string, object>
                                                {
                                                    ["text"] = qText,
                                                    ["type"] = qType,
                                                    ["options"] = optionsList,
                                                    ["answer"] = qAns
                                                });
                                            }
                                            catch { }
                                        }
                                        Console.WriteLine($"[Debug] Found {additionalQuestionsList.Count} additionalQuestions provided by AI");
                                    }

                                // FALLBACK: If modifiedQuestionsDict is empty but data has a "questions" field,
                                // it means AI returned questions in the "questions" field instead of "modifiedQuestions"
                                // This can happen if AI misunderstands the format
                                if (modifiedQuestionsDict.Count == 0 && requestedQuestionIndexes.Count > 0)
                                {
                                    Console.WriteLine($"[Debug] Fallback: Checking if data has questions field");
                                    if (data.TryGetProperty("questions", out var fallbackQsProp) && fallbackQsProp.ValueKind == JsonValueKind.Array)
                                    {
                                        var fallbackQuestions = new List<Dictionary<string, object>>();
                                        foreach (var qElem in fallbackQsProp.EnumerateArray())
                                        {
                                            try
                                            {
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
                                                    if (optionsList.Count > 0 && qAns.Contains(optionsList[0].ToUpper())) qAns = "A";
                                                    else if (optionsList.Count > 1 && qAns.Contains(optionsList[1].ToUpper())) qAns = "B";
                                                    else if (optionsList.Count > 2 && qAns.Contains(optionsList[2].ToUpper())) qAns = "C";
                                                    else if (optionsList.Count > 3 && qAns.Contains(optionsList[3].ToUpper())) qAns = "D";
                                                    else qAns = "A";
                                                }

                                                fallbackQuestions.Add(new Dictionary<string, object>
                                                {
                                                    ["text"] = qText,
                                                    ["type"] = qType,
                                                    ["options"] = optionsList,
                                                    ["answer"] = qAns
                                                });
                                            }
                                            catch { }
                                        }

                                        Console.WriteLine($"[Debug] Fallback: Found {fallbackQuestions.Count} questions in data");
                                        for (int i = 0; i < fallbackQuestions.Count && i < requestedQuestionIndexes.Count; i++)
                                        {
                                            modifiedQuestionsDict[requestedQuestionIndexes[i]] = fallbackQuestions[i];
                                        }
                                        Console.WriteLine($"[Debug] Fallback: Populated {modifiedQuestionsDict.Count} modified questions");
                                    }
                                }

                                // Now patch them into the previous exam
                                if (!string.IsNullOrEmpty(previousExamMetadata) && modifiedQuestionsDict.Count > 0)
                                {
                                    try
                                    {
                                        var prevDoc = JsonDocument.Parse(previousExamMetadata);
                                        if (prevDoc.RootElement.TryGetProperty("questions", out var prevQsProp) && prevQsProp.ValueKind == JsonValueKind.Array)
                                        {
                                            var prevQuestions = prevQsProp.EnumerateArray().ToList();
                                            var patchedQuestions = new List<Dictionary<string, object>>();

                                            for (int i = 0; i < prevQuestions.Count; i++)
                                            {
                                                int questionNum = i + 1; // 1-based index
                                                
                                                if (modifiedQuestionsDict.ContainsKey(questionNum))
                                                {
                                                    // Use modified question
                                                    patchedQuestions.Add(modifiedQuestionsDict[questionNum]);
                                                    Console.WriteLine($"[Debug] Patching question {questionNum}");
                                                }
                                                else
                                                {
                                                    // Preserve original
                                                    var prev = prevQuestions[i];
                                                    var preservedQuestion = new Dictionary<string, object>
                                                    {
                                                        ["text"] = prev.TryGetProperty("text", out var pt) ? pt.GetString() ?? "" : "",
                                                        ["type"] = prev.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice",
                                                        ["options"] = prev.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array ? popts.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>(),
                                                        ["answer"] = prev.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A"
                                                    };
                                                    patchedQuestions.Add(preservedQuestion);
                                                }
                                            }

                                                // If AI provided explicit additionalQuestions, append them
                                                if (additionalQuestionsList.Count > 0)
                                                {
                                                    patchedQuestions.AddRange(additionalQuestionsList);
                                                }

                                            // Get metadata from previous exam
                                            string title = "Đề thi";
                                            string category = "Chung";
                                            string level = "Trung cấp";
                                            int timeLimit = 30;
                                            double totalScore = 10.0;

                                            if (prevDoc.RootElement.TryGetProperty("title", out var prevTitle)) title = prevTitle.GetString() ?? title;
                                            if (prevDoc.RootElement.TryGetProperty("category", out var prevCat)) category = prevCat.GetString() ?? category;
                                            if (prevDoc.RootElement.TryGetProperty("level", out var prevLevel)) level = prevLevel.GetString() ?? level;
                                            if (prevDoc.RootElement.TryGetProperty("timeLimit", out var prevTL)) {
                                                if (prevTL.ValueKind == JsonValueKind.Number) timeLimit = prevTL.GetInt32();
                                                else if (prevTL.ValueKind == JsonValueKind.String && int.TryParse(prevTL.GetString(), out int tlVal)) timeLimit = tlVal;
                                            }
                                            if (prevDoc.RootElement.TryGetProperty("totalScore", out var prevTS)) {
                                                if (prevTS.ValueKind == JsonValueKind.Number) totalScore = prevTS.GetDouble();
                                                else if (prevTS.ValueKind == JsonValueKind.String && double.TryParse(prevTS.GetString(), out double tsVal)) totalScore = tsVal;
                                            }

                                            string message = $"Đã cập nhật {modifiedQuestionsDict.Count} câu hỏi. Bạn có muốn thay đổi thêm không?";
                                            if (data.TryGetProperty("message", out var msgProp)) {
                                                message = msgProp.GetString() ?? message;
                                            }

                                            aiText = message;
                                            metadata = JsonSerializer.Serialize(new {
                                                intent = "create_exam",
                                                title = title,
                                                category = category,
                                                level = level,
                                                timeLimit = timeLimit,
                                                totalScore = totalScore,
                                                questions = patchedQuestions,
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
                                                hasDraft = true,
                                                message = aiText,
                                                title = title,
                                                category = category,
                                                level = level,
                                                timeLimit = timeLimit,
                                                totalScore = totalScore,
                                                questions = patchedQuestions
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Debug] Error patching modified questions: {ex.Message}");
                                    }
                                }
                            }

                            aiText = data.TryGetProperty("message", out var msg) ? msg.GetString() ?? aiText : aiText;
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
                var errorMessage = ex.Message ?? string.Empty;
                if (errorMessage.Contains("RequestEntityTooLarge", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("tokens per minute", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("413", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(413, new
                    {
                        message = "Tin nhắn hoặc ngữ cảnh trò chuyện quá dài để gửi tới AI. Hãy rút gọn yêu cầu hoặc bắt đầu một cuộc trò chuyện mới."
                    });
                }

                System.Diagnostics.Debug.WriteLine($"[Chatbot Error] {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Stack Trace] {ex.StackTrace}");
                Console.Error.WriteLine($"[Chatbot Error] {ex.GetType().Name}: {ex.Message}");
                if (isExamRequest)
                {
                    var (category, count) = ParseExamIntentLocally(request.Message);
                    if (string.IsNullOrEmpty(category))
                    {
                        category = await _context.QuestionBank
                            .Select(q => q.Category)
                            .FirstOrDefaultAsync() ?? "Chung";
                    }

                    return await HandleCreateExam(category, count, count);
                }
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

        private List<int> ParseRequestedQuestionIndexes(string message)
        {
            var result = new List<int>();
            
            // Pattern 1: "câu số X", "câu X", "câu hỏi X", "câu hỏi số X"
            var regex = new System.Text.RegularExpressions.Regex(@"câu(?:\s+hỏi)?(?:\s+số)?\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(message))
            {
                if (int.TryParse(match.Groups[1].Value, out var idx))
                {
                    result.Add(idx);
                }
            }

            // Pattern 2: "question X", "q1", "Q2"
            if (result.Count == 0)
            {
                regex = new System.Text.RegularExpressions.Regex(@"question\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in regex.Matches(message))
                {
                    if (int.TryParse(match.Groups[1].Value, out var idx))
                    {
                        result.Add(idx);
                    }
                }
            }

            // Pattern 3: Single letter "Q" followed by number
            if (result.Count == 0)
            {
                regex = new System.Text.RegularExpressions.Regex(@"[qQ]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in regex.Matches(message))
                {
                    if (int.TryParse(match.Groups[1].Value, out var idx))
                    {
                        result.Add(idx);
                    }
                }
            }

            return result.Distinct().ToList();
        }

        private async Task<IActionResult> HandleCreateExam(string category, int count, int? requestedCount = null)
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

            if (newExam.Status == "Published")
            {
                var notification = new Notification
                {
                    Title = "Đề thi mới xuất bản",
                    Message = $"Đề thi \"{newExam.Title}\" ({newExam.Category} - {newExam.Level}) đã được đăng tải. Thử sức ngay!",
                    Type = "Exam",
                    TargetId = newExam.ExamId,
                    IsRead = false,
                    CreatedAt = DateTime.Now
                };
                await _context.Notifications.AddAsync(notification);
                await _context.SaveChangesAsync();
            }

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
                response = $"Tôi đã tạo xong đề thi {actualCount} câu cho chủ đề {category}. Bạn có thể xem bản thảo ở khung bên phải.",
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
                questionCountRequested = requestedCount ?? actualCount,
                questionCountActual = actualCount,
                questionCountAdjusted = requestedCount.HasValue && requestedCount.Value != actualCount,
                warning = requestedCount.HasValue && requestedCount.Value != actualCount
                    ? $"Đã tạo được {actualCount}/{requestedCount.Value} câu hỏi từ kho hiện có."
                    : null,
                intent = "create_exam"
            });
        }
    }

    public class ChatRequest
    {
        public string? Message { get; set; }
    }
}

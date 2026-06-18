using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using QuizApi.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Mở cửa Controller cho mọi user đăng nhập
    public class ChatbotController : ControllerBase
    {
        private readonly GroqService _groqService;
        private readonly QuizDbContext _context;
        private readonly FileParserService _fileParserService;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        };

        private static readonly Dictionary<string, string[]> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "C#",         new[] { "c#", "csharp", "c sharp" } },
            { ".NET Core",  new[] { "dotnet", ".net", ".net core", "dotnet core" } },
            { "IT",         new[] { "công nghệ thông tin", "cntt", "information technology", "it" } },
            { "SQL Server", new[] { "sql", "sql server", "database", "db", "t-sql", "tsql" } },
            { "ASP.NET",    new[] { "asp.net", "aspnet", "asp net" } },
            { "Toán",       new[] { "toán", "math", "toán học", "mathematics" } },
        };

        private static bool ContainsAlias(string source, string alias)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(alias)) return false;

            var rawSource = source.ToLowerInvariant();
            var rawAlias = alias.ToLowerInvariant().Trim();

            if (rawAlias is "c#" or "csharp" or "c sharp")
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    rawSource,
                    @"\bc#\b|\bcsharp\b|\bc sharp\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            if (rawAlias is "dotnet" or ".net" or ".net core" or "dotnet core")
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    rawSource,
                    @"\bdotnet\b|\.net\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            if (rawAlias is "it")
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    rawSource,
                    @"\bit\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var normalizedSource = NormalizeForMatch(source);
            var normalizedAlias = NormalizeForMatch(alias);
            if (string.IsNullOrWhiteSpace(normalizedAlias)) return false;

            if (normalizedAlias.Length >= 3)
            {
                return normalizedSource.Contains(normalizedAlias, StringComparison.Ordinal);
            }

            return rawSource.Contains(rawAlias, StringComparison.Ordinal);
        }

        private static readonly Dictionary<string, string[]> LevelAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Sơ cấp", new[] { "sơ cấp", "so cap", "dễ", "de", "cơ bản", "co ban", "elementary", "easy", "beginner", "basic" } },
            { "Trung cấp", new[] { "trung cấp", "trung cap", "trung bình", "trung binh", "intermediate", "medium" } },
            { "Cao cấp", new[] { "cao cấp", "cao cap", "khó", "kho", "advanced", "hard" } },
        };

        public ChatbotController(GroqService groqService, QuizDbContext context, FileParserService fileParserService)
        {
            _groqService = groqService;
            _context = context;
            _fileParserService = fileParserService;
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

        private static string? BuildCompactPreviousExamContext(string? metadata, List<int> requestedQuestionIndexes, int maxQuestionsToInclude = 3)
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
                        : Enumerable.Range(1, Math.Min(allQuestions.Count, Math.Max(1, maxQuestionsToInclude))).ToList();

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

                return JsonSerializer.Serialize(compactContext, _jsonOptions);
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

        private static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            string formD = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);

            foreach (char ch in formD)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(' ');
                }
            }

            var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return collapsed;
        }

        /// <summary>
        /// Trả về canonical key của category (e.g. "C#", ".NET Core", "IT") dựa trên
        /// tên category hoặc alias. Trả về null nếu không nhận ra.
        /// </summary>
        private static string? ResolveCanonicalCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;

            // Tìm match trong CategoryAliases
            foreach (var kv in CategoryAliases)
            {
                // So khớp với chính canonical key
                if (string.Equals(category.Trim(), kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;

                // So khớp với aliases
                if (kv.Value.Any(alias => string.Equals(category.Trim(), alias, StringComparison.OrdinalIgnoreCase)))
                    return kv.Key;
            }

            // Không nhận ra → trả nguyên bản (để match exact với DB)
            return category.Trim();
        }

        private static bool IsCategoryMatch(string dbCategory, string requestedCategory)
        {
            if (string.IsNullOrWhiteSpace(dbCategory)) return false;

            // Resolve cả hai về canonical key
            var requestedCanonical = ResolveCanonicalCategory(requestedCategory);
            var dbCanonical = ResolveCanonicalCategory(dbCategory);

            if (string.IsNullOrWhiteSpace(requestedCanonical)) return true; // không lọc

            // So sánh exact canonical key
            return string.Equals(dbCanonical, requestedCanonical, StringComparison.OrdinalIgnoreCase);
        }

        private static string CanonicalizeLevel(string level)
        {
            var normalizedLevel = NormalizeForMatch(level);
            foreach (var kv in LevelAliases)
            {
                var canonicalNorm = NormalizeForMatch(kv.Key);
                if (normalizedLevel == canonicalNorm || kv.Value.Select(NormalizeForMatch).Any(alias => normalizedLevel.Contains(alias, StringComparison.Ordinal)))
                {
                    return kv.Key;
                }
            }

            return "Trung cấp";
        }

        private static bool IsLevelMatch(string dbLevel, string requestedLevel)
        {
            var dbNorm = NormalizeForMatch(dbLevel);
            if (string.IsNullOrEmpty(dbNorm)) return false;

            var canonicalRequested = CanonicalizeLevel(requestedLevel);
            var allowedTerms = new HashSet<string>(StringComparer.Ordinal)
            {
                NormalizeForMatch(canonicalRequested)
            };

            if (LevelAliases.TryGetValue(canonicalRequested, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    allowedTerms.Add(NormalizeForMatch(alias));
                }
            }

            return allowedTerms.Any(term => !string.IsNullOrEmpty(term) && (dbNorm.Equals(term, StringComparison.Ordinal) || dbNorm.Contains(term, StringComparison.Ordinal) || term.Contains(dbNorm, StringComparison.Ordinal)));
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

        private static string NormalizeAnswerChoice(string? rawAnswer, List<string> options)
        {
            if (options == null || options.Count == 0)
            {
                return rawAnswer ?? string.Empty;
            }

            var normalized = (rawAnswer ?? "A").Trim().ToUpperInvariant();
            if (normalized.Length == 1 && "ABCD".Contains(normalized))
            {
                return normalized;
            }

            if (options.Count > 0 && (normalized.Contains(options[0].ToUpperInvariant(), StringComparison.Ordinal) || normalized == options[0].ToUpperInvariant())) return "A";
            if (options.Count > 1 && (normalized.Contains(options[1].ToUpperInvariant(), StringComparison.Ordinal) || normalized == options[1].ToUpperInvariant())) return "B";
            if (options.Count > 2 && (normalized.Contains(options[2].ToUpperInvariant(), StringComparison.Ordinal) || normalized == options[2].ToUpperInvariant())) return "C";
            if (options.Count > 3 && (normalized.Contains(options[3].ToUpperInvariant(), StringComparison.Ordinal) || normalized == options[3].ToUpperInvariant())) return "D";

            if (normalized.StartsWith("A", StringComparison.Ordinal) || normalized.StartsWith("OPTION A", StringComparison.Ordinal)) return "A";
            if (normalized.StartsWith("B", StringComparison.Ordinal) || normalized.StartsWith("OPTION B", StringComparison.Ordinal)) return "B";
            if (normalized.StartsWith("C", StringComparison.Ordinal) || normalized.StartsWith("OPTION C", StringComparison.Ordinal)) return "C";
            if (normalized.StartsWith("D", StringComparison.Ordinal) || normalized.StartsWith("OPTION D", StringComparison.Ordinal)) return "D";

            return "A";
        }

        private static Dictionary<string, object> BuildQuestionItem(string text, string type, List<string> options, string answer, int? questionIndex = null)
        {
            var item = new Dictionary<string, object>
            {
                ["text"] = text,
                ["type"] = type,
                ["options"] = options,
                ["answer"] = NormalizeAnswerChoice(answer, options)
            };

            if (questionIndex.HasValue)
            {
                item["questionIndex"] = questionIndex.Value;
            }

            return item;
        }

        private static bool TryParseQuestionItem(JsonElement element, out Dictionary<string, object>? questionItem, bool includeQuestionIndex = false)
        {
            questionItem = null;

            var qText = element.TryGetProperty("text", out var qt) ? qt.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(qText))
            {
                return false;
            }

            var qType = element.TryGetProperty("type", out var qtyp) ? qtyp.GetString() ?? "Multiple Choice" : "Multiple Choice";
            var optionsList = new List<string>();

            if (element.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in opts.EnumerateArray())
                {
                    optionsList.Add(opt.GetString() ?? string.Empty);
                }
            }

            var rawAnswer = element.TryGetProperty("answer", out var qans) ? qans.GetString() ?? "A" : "A";
            int? questionIndex = null;
            if (includeQuestionIndex && element.TryGetProperty("questionIndex", out var qIndexProp) && qIndexProp.ValueKind == JsonValueKind.Number && qIndexProp.TryGetInt32(out var parsedIndex))
            {
                questionIndex = parsedIndex;
            }

            questionItem = BuildQuestionItem(qText, qType, optionsList, rawAnswer, questionIndex);
            return true;
        }

        private static Dictionary<string, object> BuildQuestionItemFromStoredJson(JsonElement element)
        {
            var text = element.TryGetProperty("text", out var pt) ? pt.GetString() ?? string.Empty : string.Empty;
            var type = element.TryGetProperty("type", out var ptype) ? ptype.GetString() ?? "Multiple Choice" : "Multiple Choice";
            var options = element.TryGetProperty("options", out var popts) && popts.ValueKind == JsonValueKind.Array
                ? popts.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                : new List<string>();
            var answer = element.TryGetProperty("answer", out var pans) ? pans.GetString() ?? "A" : "A";

            return BuildQuestionItem(text, type, options, answer);
        }

        private async Task<List<QuestionBank>> GetMatchedActiveQuestions(string category, string level)
        {
            var canonicalLevel = CanonicalizeLevel(level);
            var availableQuestions = await _context.QuestionBank
                .Where(q => q.IsActive == true)
                .ToListAsync();

            return availableQuestions
                .Where(q => IsCategoryMatch(q.Category, category) && IsLevelMatch(q.Level, canonicalLevel))
                .ToList();
        }

        private async Task<List<Dictionary<string, object>>> BuildRandomQuestionItems(string category, string level, int count)
        {
            var matched = (await GetMatchedActiveQuestions(category, level))
                .OrderBy(q => Guid.NewGuid())
                .Take(count)
                .ToList();

            return matched.Select(q => new Dictionary<string, object>
            {
                ["text"] = q.Content,
                ["type"] = "Multiple Choice",
                ["options"] = new[] { q.OptionA, q.OptionB, q.OptionC, q.OptionD }.ToList(),
                ["answer"] = q.CorrectOption
            }).ToList();
        }

       [HttpPost("tutor")]
public async Task<IActionResult> Tutor([FromBody] ChatRequest request)
{
    if (string.IsNullOrEmpty(request.Message))
        return BadRequest("Tin nhắn không được để trống.");

    string currentUsername = User.Identity?.Name ?? "User";
    
    // 1. Lịch sử chat
    var history = await _context.ChatMessages
        .Where(m => m.Username == currentUsername) // Lấy cả lịch sử của AI để có ngữ cảnh đầy đủ
        .OrderByDescending(m => m.SentAt)
        .Take(10)
        .OrderBy(m => m.SentAt)
        .ToListAsync();

    string examHistoryContext = "Học viên chưa làm bài thi nào.";
    string wrongAnswersContext = "Chưa có dữ liệu câu sai gần đây.";
    
    try 
    {
        var recentExams = await (from er in _context.ExamResults
                                 join u in _context.Users on er.UserId equals u.UserId
                                 join ex in _context.Exams on er.ExamId equals ex.ExamId
                                 where u.Username == currentUsername && er.Status == "Submitted"
                                 orderby er.SubmitTime descending
                                 select new { er.ResultId, Title = ex.Title, er.Score, ex.TotalScore, er.SubmitTime })
                                 .Take(3)
                                 .ToListAsync();

        if (recentExams.Any())
        {
            var historyLines = recentExams.Select(er => $"- Đề: {er.Title} | Điểm: {er.Score}/{er.TotalScore} | Nộp lúc: {er.SubmitTime}");
            examHistoryContext = string.Join("\n", historyLines);
            var recentResultIds = recentExams.Select(e => e.ResultId).ToList();

            // Lấy dữ liệu câu sai từ các lần thi gần nhất, kết hợp với thông tin giải thích nếu có
            var wrongAnswersRaw = await (from ua in _context.UserAnswers
                                      join q in _context.QuestionBank on ua.QuestionId equals q.QuestionId
                                      join er in _context.ExamResults on ua.ResultId equals er.ResultId
                                      join ex in _context.Exams on er.ExamId equals ex.ExamId
                                      join eq in _context.ExamQuestions on new { er.ExamId, ua.QuestionId } equals new { eq.ExamId, eq.QuestionId }
                                      where recentResultIds.Contains(ua.ResultId) 
                                            && ua.SelectedOption != q.CorrectOption 
                                      select new {
                                          er.ResultId,
                                          ExamTitle = ex.Title,
                                          q.Content,
                                          ua.SelectedOption,
                                          q.CorrectOption,
                                          OptionA = q.OptionA,
                                          OptionB = q.OptionB,
                                          OptionC = q.OptionC,
                                          OptionD = q.OptionD,
                                          q.Explanation,
                                          OrderIndex = eq.OrderIndex
                                      }).ToListAsync();

            if (wrongAnswersRaw.Any()) {
                var formattedWrongs = new List<string>();
                
                // Nhóm theo ResultId để tách biệt các lần nộp bài khác nhau
                var groupedWrongs = wrongAnswersRaw.GroupBy(w => new { w.ResultId, w.ExamTitle });
                
                // Chỉ lấy BÀI THI GẦN NHẤT để tránh AI bị ngợp và lặp câu hỏi
                var latestGroup = groupedWrongs.OrderByDescending(g => g.Key.ResultId).First();
                
                formattedWrongs.Add($"\n[ĐỀ THI: {latestGroup.Key.ExamTitle.ToUpper()}]");
                
                var uniqueQuestions = latestGroup.DistinctBy(q => q.Content).OrderBy(q => q.OrderIndex).Take(5);

                foreach(var item in uniqueQuestions) 
                {
                    string GetOptionText(string? opt) {
                        return (opt?.ToUpper().Trim()) switch {
                            "A" => item.OptionA ?? "(Không có nội dung)",
                            "B" => item.OptionB ?? "(Không có nội dung)",
                            "C" => item.OptionC ?? "(Không có nội dung)",
                            "D" => item.OptionD ?? "(Không có nội dung)",
                            _ => "(Chưa chọn)"
                        };
                    }
                    string userChoice = GetOptionText(item.SelectedOption);
                    string correctChoice = GetOptionText(item.CorrectOption);
                    
                    string explanationText = !string.IsNullOrWhiteSpace(item.Explanation) 
                        ? $"\n  + [NGUỒN_GIẢNG_BÀI]: \"{item.Explanation}\"" 
                        : "\n  + [NGUỒN_GIẢNG_BÀI]: [TRỐNG] -> LỆNH BẮT BUỘC: Bạn CHỈ ĐƯỢC PHÉP trả lời đúng 1 câu 'Hệ thống sẽ sớm cập nhật.' Tuyệt đối không tự suy luận giải thích.";

                    formattedWrongs.Add($"CÂU SỐ {item.OrderIndex}: \"{item.Content}\"\n  + Học viên chọn ({item.SelectedOption ?? "Trống"}): \"{userChoice}\"\n  + Đáp án đúng ({item.CorrectOption}): \"{correctChoice}\"{explanationText}");
                }
                wrongAnswersContext = string.Join("\n\n", formattedWrongs);
            } else {
                wrongAnswersContext = "Bài thi gần nhất học viên làm đúng 100% hoặc hệ thống chưa ghi nhận lỗi sai.";
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Lỗi RAG Dữ liệu thi]: {ex.Message}");
    }  
    
    // 1. Xử lý ĐỀ ĐÃ LÀM: Gom nhóm để lấy được ResultId của lần thi gần nhất
    var attemptedExams = await (from er in _context.ExamResults
                                join u in _context.Users on er.UserId equals u.UserId
                                join ex in _context.Exams on er.ExamId equals ex.ExamId
                                where u.Username == currentUsername && er.Status == "Submitted"
                                group er by new { ex.ExamId, ex.Title, ex.Category } into g
                                select new {
                                    ExamId = g.Key.ExamId,
                                    Title = g.Key.Title,
                                    Category = g.Key.Category,
                                    // Lấy ID của lần nộp bài mới nhất để làm link kết quả
                                    LatestResultId = g.Max(x => x.ResultId) 
                                }).ToListAsync();

    var attemptedExamIds = attemptedExams.Select(e => e.ExamId).ToList();

    // 2. Xử lý ĐỀ CHƯA LÀM
    var unattemptedExams = await _context.Exams
                                .Where(e => e.Status == "Published" && !attemptedExamIds.Contains(e.ExamId))
                                .ToListAsync();

    // 3. Ghép Link 
    // Đề CHƯA LÀM -> Bắt buộc ra trang Danh sách đề để bấm nút tạo ResultId
    var unattemptedText = string.Join("\n", unattemptedExams.Select(e => $"[CARD_EXAM|{e.ExamId}|{e.Title}|{e.Category}]"));

    // Đề ĐÃ LÀM -> Trỏ thẳng vào trang Kết quả bằng LatestResultId
    var attemptedText = string.Join("\n", attemptedExams.Select(e => $"[CARD_RESULT|{e.LatestResultId}|{e.Title}|{e.Category}]"));

    string examLinksContext = $@"
[ĐỀ THI CHƯA LÀM]:
{(unattemptedExams.Any() ? unattemptedText : "Không có đề thi nào chưa làm.")}

[ĐỀ THI ĐÃ LÀM]:
{(attemptedExams.Any() ? attemptedText : "Chưa làm đề thi nào.")}
";
    // Luật train chatbot AI
    string systemPrompt = $@"You are 'QuizChat AI Tutor', an empathetic, friendly, and expert programming teacher.

<student_memory>
- Lịch sử thi:
{examHistoryContext}
- Các câu vừa làm sai:
{wrongAnswersContext}
- DANH MỤC ĐỀ THI HIỆN CÓ:
{examLinksContext}
</student_memory>

<strict_rules>
1. TỰ NHIÊN: Xưng ""Mình"" và ""Bạn"". TUYỆT ĐỐI KHÔNG để lộ các thẻ nội bộ ra ngoài.
2. ĐÁNH SỐ CÂU: BẮT BUỘC gọi đúng ""CÂU SỐ X"".
3. TRÌNH BÀY ĐỒNG NHẤT: Trình bày các câu sai giống hệt nhau. 
4. NGUỒN GIẢI THÍCH: Chỉ dùng kiến thức trong [NGUỒN_GIẢNG_BÀI]. Nếu ghi [TRỐNG], BẮT BUỘC trả lời: ""Hệ thống sẽ sớm cập nhật.""
5. TƯ VẤN ĐỀ THI (QUAN TRỌNG): Khi người dùng yêu cầu tìm đề thi (chưa làm/đã làm, có thể kèm tên môn cụ thể), hãy tìm trong ""DANH MỤC ĐỀ THI HIỆN CÓ"" và liệt kê ra. 
   - Nếu có tên môn, CHỈ lọc các đề của môn đó. Nếu không có môn nào khớp, xin lỗi khéo léo.
   - BẮT BUỘC in ra đúng định dạng link Markdown mà hệ thống cung cấp (Ví dụ: [Tên đề thi](/user/exams?examId=1)). Tuyệt đối không tự bịa ra link.
6. GIỚI HẠN PHẠM VI: Chỉ trả lời các câu hỏi về IT và bài thi.
7. BẢO MẬT: CẤM tự tạo câu hỏi mới trong luồng chat này.
</strict_rules>";

    try
    {
        var aiRawResponse = await _groqService.ChatAsync(systemPrompt, history, request.Message);
        
        var chatMsg = new ChatMessage
        {
            Username = currentUsername,
            UserMessage = request.Message,
            AiResponse = aiRawResponse,
            SentAt = DateTime.UtcNow,
            //Metadata = "tutor_chat_v6" 
        };
        _context.ChatMessages.Add(chatMsg);
        await _context.SaveChangesAsync();

        return Ok(new { message = aiRawResponse });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] AI Tutor: {ex.Message}");
        return StatusCode(500, new { message = "Xin lỗi, kết nối mạng đang gián đoạn. Vui lòng thử lại." });
    }
}

        [HttpPost("ask")]
        [Authorize(Roles = "Admin")] // Chỉ Admin mới được tạo đề
        public async Task<IActionResult> Ask([FromBody] ChatRequest request)
        {
            if (string.IsNullOrEmpty(request.Message) && string.IsNullOrEmpty(request.FileContent))
                return BadRequest("Tin nhắn không được để trống.");
            // Nếu chỉ có file mà không có message, tự thêm lệnh mặc định
            if (string.IsNullOrEmpty(request.Message))
                request.Message = "Hãy tạo đề thi từ nội dung tài liệu đính kèm.";

            string userMsg = request.Message.ToLower();
            bool isExamRequest = userMsg.Contains("tạo đề") || userMsg.Contains("soạn đề") || userMsg.Contains("generate exam") || userMsg.Contains("create exam") || userMsg.Contains("đề thi") || userMsg.Contains("quiz");
            var requestedQuestionIndexes = ParseRequestedQuestionIndexes(request.Message);
            var requestedQuestionCount = ParseRequestedQuestionCount(request.Message);

            var stats = await _context.QuestionBank
                .GroupBy(q => q.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();
            string dbStats = string.Join(", ", stats.Select(s => $"{s.Category}: {s.Count} câu"));
            // Nếu có file đính kèm, ghi chú rõ để AI không dựa vào DB
            if (!string.IsNullOrWhiteSpace(request.FileContent))
            {
                dbStats = dbStats + " [CHÚ Ý: Người dùng đã đính kèm tài liệu - BỎ QUA database stats, tạo câu hỏi HOÀN TOÀN từ nội dung tài liệu đính kèm]";  
            }

            string currentUsername = User.Identity?.Name ?? "Admin";
            var history = await _context.ChatMessages
                .Where(m => m.Username == currentUsername)
                .OrderByDescending(m => m.SentAt)
                .Take(10)
                .OrderBy(m => m.SentAt)
                .ToListAsync();
            var compactHistory = BuildCompactHistory(history, 3);
            bool isExamModificationRequest = userMsg.Contains("thay đổi câu") || userMsg.Contains("đổi câu") || userMsg.Contains("sửa câu") || userMsg.Contains("chỉnh sửa câu") || userMsg.Contains("đổi đề") || userMsg.Contains("thay đổi đề") || userMsg.Contains("sửa đề") || userMsg.Contains("thay đổi độ khó") || userMsg.Contains("giảm độ khó") || userMsg.Contains("tăng độ khó") || userMsg.Contains("thêm câu") || userMsg.Contains("thêm 1 câu") || userMsg.Contains("thêm 2 câu") || userMsg.Contains("thêm 3 câu") || userMsg.Contains("bổ sung câu") || userMsg.Contains("cộng thêm câu") || userMsg.Contains("mở rộng đề") || userMsg.Contains("nâng lên") || userMsg.Contains("tăng lên");
            
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
            int previousExamQuestionLimit = (isExamModificationRequest && requestedQuestionCount.GetValueOrDefault() > 0 && requestedQuestionIndexes.Count == 0) ? 2 : 3;
            string? compactPreviousExamContext = BuildCompactPreviousExamContext(previousExamMetadata, requestedQuestionIndexes, previousExamQuestionLimit);
            // If the user explicitly requested to "tạo đề" / create an exam AND NO FILE IS ATTACHED, use DB-only flow and do not call AI.
            if (isExamRequest && string.IsNullOrWhiteSpace(request.FileContent))
            {
                var (categoryLocal, countLocal, levelLocal) = ParseExamIntentLocally(request.Message);
                if (string.IsNullOrEmpty(categoryLocal))
                {
                    categoryLocal = await _context.QuestionBank
                        .Select(q => q.Category)
                        .FirstOrDefaultAsync() ?? "Chung";
                }

                return await HandleCreateExam(categoryLocal, levelLocal, countLocal, countLocal, request.Message);
            }

            // Lấy danh sách category thực tế từ DB để đưa vào prompt
            var allowedCategories = await _context.QuestionBank
                .Where(q => !string.IsNullOrEmpty(q.Category))
                .Select(q => q.Category)
                .Distinct()
                .ToListAsync();
            string allowedCategoriesStr = allowedCategories.Count > 0
                ? string.Join(", ", allowedCategories.Select(c => $"\"{c}\""))
                : "\"Chung\"";

            string systemPrompt = $@"You are the 'QuizChat Local AI Tutor'.
CURRENT DATABASE STATS: {dbStats}
ALLOWED CATEGORIES (MANDATORY - use EXACTLY one of these for the ""category"" field): [{allowedCategoriesStr}]

STRICT RULES:
1. You are an expert in the categories listed above. 
2. If the user asks for an exam, you SHOULD use existing question counts as a reference, but you ARE encouraged to generate NEW high-quality questions to fulfill the request.
3. You CANNOT provide information from the external world (news, etc.).
4. CONTEXT MEMORY: Use the previous conversation history provided in the chat logs to stay on track.
5. ALWAYS respond with valid JSON. Do not include any text outside the JSON.
6. EXAM JSON RULE: For each question, the ""answer"" field MUST be exactly one character: ""A"", ""B"", ""C"", or ""D"". DO NOT put the full text of the answer in the ""answer"" field.
7. EXAM LEVEL RULE: You must determine the difficulty level requested by the user (""Sơ cấp"" for basic/elementary, ""Trung cấp"" for intermediate, ""Cao cấp"" for advanced) and include it in the ""level"" property of the JSON. If not specified, default to ""Trung cấp"".
7a. EXAMPLE LEVEL GUIDELINES: Use the examples below to match the tone and difficulty of generated questions. STRICTLY follow these styles when generating questions for each level.
    - Sơ cấp (Easy): short, direct questions testing basic facts or simple calculations. Example: ""Tìm x: 2x + 5 = 11"" with simple numeric options.
    - Trung cấp (Intermediate): multi-step problems or applied knowledge requiring short reasoning. Example: ""Cho tam giác vuông có hai cạnh góc vuông 3cm và 4cm. Tìm độ dài cạnh huyền."" with plausible distractors.
    - Cao cấp (Advanced): conceptual or multi-part problems requiring deeper reasoning, proofs, or code-level understanding. Example: ""Cho hàm f(x)=x^3-3x+1, chứng minh rằng f có đúng một nghiệm thực và ước lượng nghiệm đó."" 
    Note: When possible, include short context or constraints for Trung cấp/Cao cấp to avoid ambiguous or trivial items.
8. If the user asks to create or generate a quiz or exam (e.g. ""tạo đề"", ""soạn đề"", ""generate exam"", ""create quiz""), you MUST set ""intent"": ""create_exam"" and generate the full questions array immediately. Do not ask clarifying questions first.
8a. If the user specifies a number of questions, generate exactly that many questions. Do not generate more or fewer.
9. PROACTIVE SUGGESTION RULE: Whenever you generate or modify an exam (intent: ""create_exam""), the ""message"" property in your JSON MUST ALWAYS end with a friendly question in Vietnamese asking the user if they want to modify a specific question, change the difficulty, or randomize a completely new set of questions. (Example: ""Bạn có muốn thay đổi câu hỏi nào không, hoặc tạo một đề thi khác?"").
10. NO MARKDOWN: Output ONLY raw JSON text. DO NOT wrap the response in ```json or any markdown formatting.
11. MODIFICATION WITHOUT FULL REGEN: If user only wants to modify 1 or a few specific questions (e.g., ""sửa câu 1"", ""thay đổi câu 2 và 3""), do NOT regenerate the entire exam. Instead, return ONLY the specific modified question(s) with their new content. The backend will handle patching them back into the full exam automatically.
12. DOCUMENT CONTEXT RULE: If a DOCUMENT_CONTEXT section is provided below, use its content as the primary source of knowledge for generating exam questions. Questions MUST be based on and derived from the document content. Treat the document as the study material.

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

            // Đưa nội dung tài liệu vào system prompt nếu user gửi kèm file
            if (!string.IsNullOrWhiteSpace(request.FileContent))
            {
                var truncatedFileContent = request.FileContent.Length > 30000
                    ? request.FileContent.Substring(0, 30000) + "\n...[Nội dung tài liệu bị cắt bớt do quá dài]"
                    : request.FileContent;
                systemPrompt += $@"

DOCUMENT_CONTEXT (Tệp đính kèm: {request.FileName ?? "tài liệu"}):
---BEGIN DOCUMENT---
{truncatedFileContent}
---END DOCUMENT---

DOCUMENT PROCESSING RULES (ĐỌC KỸ - BẮT BUỘC TUÂN THỦ):
1. EXTRACT QUESTIONS: Đọc toàn bộ nội dung tài liệu, trích xuất tất cả các câu hỏi/bài tập.
2. ANSWERS FROM USER MESSAGE: Nếu tài liệu KHÔNG có đáp án, hãy đọc tin nhắn của người dùng để tìm đáp án bổ sung. Ví dụ: người dùng có thể cung cấp ""Đáp án: 1-A, 2-C, 3-B..."" hoặc ""Câu 1: A, Câu 2: D..."".
3. LEVEL FROM USER MESSAGE: Nếu tài liệu KHÔNG xác định độ khó, đọc tin nhắn của người dùng để lấy level (""sơ cấp"", ""trung cấp"", ""cao cấp""). Nếu không có, mặc định là ""Trung cấp"".
4. INFER MISSING ANSWERS: Nếu cả tài liệu lẫn tin nhắn đều không có đáp án, hãy tự suy luận đáp án đúng dựa vào nội dung câu hỏi và các lựa chọn.
5. MERGE INFORMATION: Kết hợp thông tin từ TÀI LIỆU (câu hỏi, lựa chọn) + TIN NHẮN NGƯỜI DÙNG (đáp án, level, số câu cần tạo) để tạo đề thi hoàn chỉnh.
6. FORMAT OPTIONS: Nếu tài liệu có câu hỏi nhưng không có 4 lựa chọn A/B/C/D, hãy TỰ TẠO các lựa chọn phù hợp dựa trên nội dung câu hỏi.
7. ALL QUESTIONS FROM DOCUMENT: Tất cả câu hỏi trong JSON phải được lấy/dựa trên nội dung tài liệu, không được tự nghĩ câu hỏi mới trừ khi người dùng yêu cầu thêm câu.";
            }

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
                // Khi có file đính kèm, đưa nội dung file TRỰC TIẾP vào phần user message
                // thay vì chỉ để trong system prompt (AI sẽ chắc chắn đọc được)
                string userMessageForGroq = request.Message;
                if (!string.IsNullOrWhiteSpace(request.FileContent))
                {
                    var truncatedContent = request.FileContent.Length > 28000
                        ? request.FileContent.Substring(0, 28000) + "\n...[Nội dung bị cắt bớt]"
                        : request.FileContent;

                    var fileName = request.FileName ?? "tài liệu";
                    userMessageForGroq =
                        $"[NỘI DUNG TÀI LIỆU ĐỀ THI - Tệp: {fileName}]\n" +
                        "---\n" +
                        truncatedContent + "\n" +
                        "---\n\n" +
                        "[YÊU CẦU CỦA NGƯỜI DÙNG]:\n" +
                        request.Message + "\n\n" +
                        "LƯU Ý: Hãy trích xuất câu hỏi từ tài liệu trên. Nếu người dùng cung cấp đáp án trong phần YÊU CẦU, hãy sử dụng đáp án đó. Tạo JSON đề thi với intent \"create_exam\".";
                }

                var aiRawResponse = await _groqService.ChatAsync(systemPrompt, compactHistory, userMessageForGroq);
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
                            // Resolve canonical (hardcoded aliases)
                            category = ResolveCanonicalCategory(category) ?? category;
                            // Bắt buộc phải map vào category có sẵn trong DB — không tạo mới
                            var dbCategories = await _context.QuestionBank
                                .Where(q => !string.IsNullOrEmpty(q.Category))
                                .Select(q => q.Category)
                                .Distinct()
                                .ToListAsync();
                            if (dbCategories.Count > 0)
                            {
                                // 1. Exact match (case-insensitive)
                                var exactMatch = dbCategories.FirstOrDefault(c =>
                                    string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
                                if (exactMatch != null)
                                {
                                    category = exactMatch;
                                }
                                else
                                {
                                    // 2. Canonical match qua IsCategoryMatch
                                    var canonicalMatch = dbCategories.FirstOrDefault(c => IsCategoryMatch(c, category));
                                    if (canonicalMatch != null)
                                    {
                                        category = canonicalMatch;
                                    }
                                    else
                                    {
                                        // 3. Fallback: category đầu tiên trong DB (không để sinh mới)
                                        category = dbCategories.First();
                                    }
                                }
                            }

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
                            // If user explicitly requested a number of questions, prefer using existing DB questions of the same level
                            // when enough are available. This avoids AI generating off-level or low-quality questions.
                            // SKIP this DB-first path if user attached a file - always use AI-generated questions from the document.
                            if (requestedCount > 0 && string.IsNullOrWhiteSpace(request.FileContent))
                            {
                                var canonicalLevel = CanonicalizeLevel(level);
                                var dbCount = (await GetMatchedActiveQuestions(category, canonicalLevel)).Count;
                                if (dbCount >= requestedCount)
                                {
                                    // Use DB-sourced random questions
                                    validQuestionsList = await BuildRandomQuestionItems(category, canonicalLevel, requestedCount);
                                    questionCountActual = validQuestionsList.Count;
                                    questionCountAdjusted = false;
                                }
                            }
                            // Chỉ dùng câu hỏi từ AI nếu DB chưa đủ số lượng cần thiết
                            // (tránh trộn câu hỏi sai chủ đề do AI sinh ra)
                            bool dbAlreadyFulfilled = requestedCount > 0 && string.IsNullOrWhiteSpace(request.FileContent) && validQuestionsList.Count >= requestedCount;
                            if (!dbAlreadyFulfilled && data.TryGetProperty("questions", out var questionsProp) && questionsProp.ValueKind == JsonValueKind.Array) {
                                foreach (var qElem in questionsProp.EnumerateArray()) {
                                    try {
                                        if (TryParseQuestionItem(qElem, out var questionItem, includeQuestionIndex: true) && questionItem != null)
                                        {
                                            validQuestionsList.Add(questionItem);
                                        }
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
                                    // Nếu có file đính kèm, dùng câu AI đã sinh dù thiếu (không fallback DB)
                                    if (!string.IsNullOrWhiteSpace(request.FileContent))
                                    {
                                        // Giữ nguyên những câu AI sinh từ file
                                    }
                                    else
                                    {
                                        // Not enough AI-provided questions; fallback to DB-created exam
                                        return await HandleCreateExam(category, level, requestedCount, requestedCount, request.Message);
                                    }
                                }
                            }

                            questionCountActual = validQuestionsList.Count;
                            if (questionCountRequested > 0 && questionCountActual != questionCountRequested)
                            {
                                questionCountAdjusted = true;
                            }

                            // Assign ảnh Cloudinary vào câu hỏi theo thứ tự (ảnh PDF xuất hiện theo vị trí)
                            if (request.ImageUrls != null && request.ImageUrls.Count > 0 && !string.IsNullOrWhiteSpace(request.FileContent))
                            {
                                int imgIdx = 0;
                                for (int qi = 0; qi < validQuestionsList.Count && imgIdx < request.ImageUrls.Count; qi++)
                                {
                                    validQuestionsList[qi]["imageUrl"] = request.ImageUrls[imgIdx++];
                                }
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
                                                    patchedQuestions.Add(BuildQuestionItemFromStoredJson(prev));
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
                                                                                var extra = await BuildRandomQuestionItems(category, level, toAdd);
                                                                                var patched = prevQuestions.Select(BuildQuestionItemFromStoredJson).ToList();

                                                                                patched.AddRange(extra);
                                                                                validQuestionsList = patched;
                                                                            }
                                                                            else
                                                                            {
                                                                                // Fallback: preserve previous exam
                                                                                validQuestionsList = prevQuestions.Select(BuildQuestionItemFromStoredJson).ToList();
                                                                            }
                                                                        }
                                                                        else
                                                                        {
                                                                            // Preserve the entire previous exam for safety.
                                                                            validQuestionsList = prevQuestions.Select(BuildQuestionItemFromStoredJson).ToList();
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
                            }, _jsonOptions);

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
                            Console.WriteLine($"[Debug] Processing modify_questions intent");

                            var modifiedQuestionsDict = new Dictionary<int, Dictionary<string, object>>();
                            if (data.TryGetProperty("modifiedQuestions", out var modifiedQsProp) && modifiedQsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var mqElem in modifiedQsProp.EnumerateArray())
                                {
                                    try
                                    {
                                        if (!mqElem.TryGetProperty("index", out var indexProp))
                                            continue;

                                        int? idx = null;
                                        if (indexProp.ValueKind == JsonValueKind.Number && indexProp.TryGetInt32(out var numericIndex))
                                        {
                                            idx = numericIndex;
                                        }
                                        else if (indexProp.ValueKind == JsonValueKind.String && int.TryParse(indexProp.GetString(), out var stringIndex))
                                        {
                                            idx = stringIndex;
                                        }

                                        if (!idx.HasValue)
                                            continue;

                                        if (TryParseQuestionItem(mqElem, out var parsedItem) && parsedItem != null)
                                        {
                                            modifiedQuestionsDict[idx.Value] = parsedItem;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            var additionalQuestionsList = new List<Dictionary<string, object>>();
                            if (data.TryGetProperty("additionalQuestions", out var additionalQsProp) && additionalQsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var aq in additionalQsProp.EnumerateArray())
                                {
                                    try
                                    {
                                        if (TryParseQuestionItem(aq, out var parsedAdditional) && parsedAdditional != null)
                                        {
                                            additionalQuestionsList.Add(parsedAdditional);
                                        }
                                    }
                                    catch { }
                                }
                            }

                            Console.WriteLine($"[Debug] Modified {modifiedQuestionsDict.Count} questions");
                            Console.WriteLine($"[Debug] Found {additionalQuestionsList.Count} additionalQuestions provided by AI");

                            if (modifiedQuestionsDict.Count == 0 && additionalQuestionsList.Count == 0)
                            {
                                Console.WriteLine("[Debug] No modifiedQuestions or additionalQuestions found in AI response");
                            }

                            if (modifiedQuestionsDict.Count == 0 && requestedQuestionIndexes.Count > 0)
                            {
                                Console.WriteLine($"[Debug] Fallback: Checking if data has questions field");
                                var fallbackQuestions = new List<Dictionary<string, object>>();
                                if (data.TryGetProperty("modifiedQuestions", out var modifiedQsFallbackProp) && modifiedQsFallbackProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var qElem in modifiedQsFallbackProp.EnumerateArray())
                                    {
                                        try
                                        {
                                            if (TryParseQuestionItem(qElem, out var fallbackItem) && fallbackItem != null)
                                            {
                                                fallbackQuestions.Add(fallbackItem);
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                if (fallbackQuestions.Count == 0 && data.TryGetProperty("questions", out var fallbackQsProp) && fallbackQsProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var qElem in fallbackQsProp.EnumerateArray())
                                    {
                                        try
                                        {
                                            if (TryParseQuestionItem(qElem, out var fallbackItem) && fallbackItem != null)
                                            {
                                                fallbackQuestions.Add(fallbackItem);
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                Console.WriteLine($"[Debug] Fallback: Found {fallbackQuestions.Count} questions in data");
                                for (int i = 0; i < fallbackQuestions.Count && i < requestedQuestionIndexes.Count; i++)
                                {
                                    modifiedQuestionsDict[requestedQuestionIndexes[i]] = fallbackQuestions[i];
                                }

                                Console.WriteLine($"[Debug] Fallback: Populated {modifiedQuestionsDict.Count} modified questions");
                            }

                            if (!string.IsNullOrEmpty(previousExamMetadata) && (modifiedQuestionsDict.Count > 0 || additionalQuestionsList.Count > 0))
                            {
                                try
                                {
                                    var prevDoc = JsonDocument.Parse(previousExamMetadata);
                                    if (prevDoc.RootElement.TryGetProperty("questions", out var prevQsProp) && prevQsProp.ValueKind == JsonValueKind.Array)
                                    {
                                        var prevQuestions = prevQsProp.EnumerateArray().ToList();
                                        var patchedQuestions = new List<Dictionary<string, object>>();
                                        var appendedQuestions = new List<KeyValuePair<int, Dictionary<string, object>>>();

                                        for (int i = 0; i < prevQuestions.Count; i++)
                                        {
                                            int questionNum = i + 1;
                                            if (modifiedQuestionsDict.ContainsKey(questionNum))
                                            {
                                                patchedQuestions.Add(modifiedQuestionsDict[questionNum]);
                                                Console.WriteLine($"[Debug] Patching question {questionNum}");
                                            }
                                            else
                                            {
                                                patchedQuestions.Add(BuildQuestionItemFromStoredJson(prevQuestions[i]));
                                            }
                                        }

                                        if (additionalQuestionsList.Count > 0)
                                        {
                                            patchedQuestions.AddRange(additionalQuestionsList);
                                        }

                                        if (modifiedQuestionsDict.Count > 0)
                                        {
                                            foreach (var extraQuestion in modifiedQuestionsDict
                                                .Where(pair => pair.Key > prevQuestions.Count)
                                                .OrderBy(pair => pair.Key))
                                            {
                                                appendedQuestions.Add(extraQuestion);
                                            }

                                            if (appendedQuestions.Count > 0)
                                            {
                                                patchedQuestions.AddRange(appendedQuestions.Select(pair => pair.Value));
                                            }
                                        }

                                        string title = "Đề thi";
                                        string category = "Chung";
                                        string level = "Trung cấp";
                                        int timeLimit = 30;
                                        double totalScore = 10.0;

                                        if (prevDoc.RootElement.TryGetProperty("title", out var prevTitle)) title = prevTitle.GetString() ?? title;
                                        if (prevDoc.RootElement.TryGetProperty("category", out var prevCat)) category = prevCat.GetString() ?? category;
                                        if (prevDoc.RootElement.TryGetProperty("level", out var prevLevel)) level = prevLevel.GetString() ?? level;
                                        if (prevDoc.RootElement.TryGetProperty("timeLimit", out var prevTL))
                                        {
                                            if (prevTL.ValueKind == JsonValueKind.Number) timeLimit = prevTL.GetInt32();
                                            else if (prevTL.ValueKind == JsonValueKind.String && int.TryParse(prevTL.GetString(), out int tlVal)) timeLimit = tlVal;
                                        }
                                        if (prevDoc.RootElement.TryGetProperty("totalScore", out var prevTS))
                                        {
                                            if (prevTS.ValueKind == JsonValueKind.Number) totalScore = prevTS.GetDouble();
                                            else if (prevTS.ValueKind == JsonValueKind.String && double.TryParse(prevTS.GetString(), out double tsVal)) totalScore = tsVal;
                                        }
                                        int totalAddedQuestions = additionalQuestionsList.Count + appendedQuestions.Count;
                                        int totalModifiedQuestions = modifiedQuestionsDict.Count - appendedQuestions.Count;
                                        string message = totalAddedQuestions > 0
                                            ? $"Đã thêm {totalAddedQuestions} câu hỏi mới vào đề. Bạn có muốn chỉnh sửa thêm không?"
                                            : $"Đã cập nhật {totalModifiedQuestions} câu hỏi. Bạn có muốn thay đổi thêm không?";
                                        if (data.TryGetProperty("message", out var msgProp))
                                        {
                                            message = msgProp.GetString() ?? message;
                                        }

                                        aiText = message;
                                        metadata = JsonSerializer.Serialize(new
                                        {
                                            intent = "create_exam",
                                            title,
                                            category,
                                            level,
                                            timeLimit,
                                            totalScore,
                                            questions = patchedQuestions,
                                            message
                                        }, _jsonOptions);

                                        var chatMsg = new ChatMessage
                                        {
                                            Username = User.Identity?.Name ?? "Admin",
                                            UserMessage = request.Message,
                                            AiResponse = aiText,
                                            Metadata = metadata
                                        };
                                        _context.ChatMessages.Add(chatMsg);
                                        await _context.SaveChangesAsync();

                                        return Ok(new
                                        {
                                            intent = "create_exam",
                                            hasDraft = true,
                                            message = aiText,
                                            title,
                                            category,
                                            level,
                                            timeLimit,
                                            totalScore,
                                            questions = patchedQuestions
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Debug] Error patching modified questions: {ex.Message}");
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
                    var (category, count, level) = ParseExamIntentLocally(request.Message);
                    if (string.IsNullOrEmpty(category))
                    {
                        category = await _context.QuestionBank
                            .Select(q => q.Category)
                            .FirstOrDefaultAsync() ?? "Chung";
                    }

                    return await HandleCreateExam(category, level, count, count, request.Message);
                }
                return Ok(new { message = "Xin lỗi, tôi gặp một chút trục trặc khi kết nối với bộ não AI. Vui lòng thử lại sau giây lát.", hasDraft = false });
            }
        }

        private (string Category, int Count, string Level) ParseExamIntentLocally(string message)
        {
            var countMatch = System.Text.RegularExpressions.Regex.Match(message, @"(\d+)\s*câu");
            int count = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 10;
            string level = CanonicalizeLevel(message);

            // Tìm category (tìm các keyword phổ biến)
            string category = "";

            string msgLower = NormalizeForMatch(message);
            foreach (var kv in CategoryAliases)
            {
                bool matched = false;
                foreach (var alias in kv.Value)
                {
                    if (ContainsAlias(message, alias))
                    {
                        matched = true;
                        break;
                    }
                }
                
                if (matched)
                {
                    category = kv.Key;
                    break;
                }
            }

            return (category, count, level);
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

            // Pattern 4: number before "câu" or "câu hỏi", often used in modification requests like "sửa 1 câu"
            if (result.Count == 0)
            {
                regex = new System.Text.RegularExpressions.Regex(@"(\d+)\s*(?:câu|cau|câu hỏi|question|questions)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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

        private async Task<IActionResult> HandleCreateExam(string category, string level, int count, int? requestedCount = null, string? userMessage = null)
        {
            var canonicalLevel = CanonicalizeLevel(level);
            var matchedQuestions = await GetMatchedActiveQuestions(category, canonicalLevel);

            if (matchedQuestions.Count == 0)
            {
                // Kiểm tra xem có phải do thiếu độ khó không
                var categoryQuestions = await _context.QuestionBank
                    .Where(q => q.IsActive == true)
                    .ToListAsync();
                bool hasCategory = categoryQuestions.Any(q => IsCategoryMatch(q.Category, category));

                if (hasCategory)
                {
                    return Ok(new { response = $"Tôi xin lỗi, hiện tại trong kho không có câu hỏi nào thuộc chủ đề '{category}' ở độ khó '{canonicalLevel}'." });
                }
                return Ok(new { response = $"Tôi xin lỗi, hiện tại trong kho không có câu hỏi nào thuộc chủ đề '{category}'." });
            }

            int actualCount = Math.Min(count, matchedQuestions.Count);

            var randomQuestions = matchedQuestions
                .OrderBy(q => Guid.NewGuid())
                .Take(actualCount)
                .ToList();
            double totalScore = Math.Round(randomQuestions.Sum(q => q.ScorePerQuestion), 2);
            var newExam = new Exam
            {
                Title = $"Đề thi {category} (Tạo bởi AI)",
                Description = $"Đề thi tự động gồm {actualCount} câu hỏi chủ đề {category}.",
                Category = category,
                Level = canonicalLevel,
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

            var questionsForMetadata = randomQuestions.Select(q => {
                var optionsList = new List<string>();
                if (!string.IsNullOrEmpty(q.OptionA)) optionsList.Add(q.OptionA);
                if (!string.IsNullOrEmpty(q.OptionB)) optionsList.Add(q.OptionB);
                if (!string.IsNullOrEmpty(q.OptionC)) optionsList.Add(q.OptionC);
                if (!string.IsNullOrEmpty(q.OptionD)) optionsList.Add(q.OptionD);
                
                return new {
                    id = q.QuestionId,
                    text = q.Content,
                    type = optionsList.Count > 0 ? "Multiple Choice" : "Một câu hỏi ngắn",
                    options = optionsList,
                    answer = NormalizeAnswerChoice(q.CorrectOption, optionsList)
                };
            }).ToList();

            var responseMessage = $"Tôi đã tạo xong đề thi {actualCount} câu cho chủ đề {category}. Bạn có thể xem bản thảo ở khung bên phải.";

            var metadata = JsonSerializer.Serialize(new {
                intent = "create_exam",
                title = newExam.Title,
                category = newExam.Category,
                level = newExam.Level,
                timeLimit = newExam.TimeLimit,
                totalScore = newExam.TotalScore,
                questions = questionsForMetadata,
                message = responseMessage
            }, _jsonOptions);

            var chatMsg = new ChatMessage {
                Username = User.Identity?.Name ?? "Admin",
                UserMessage = userMessage ?? $"Tạo đề thi {category} {level} {actualCount} câu",
                AiResponse = responseMessage,
                Metadata = metadata
            };
            _context.ChatMessages.Add(chatMsg);
            await _context.SaveChangesAsync();

            return Ok(new { 
                response = responseMessage,
                examId = newExam.ExamId,
                title = newExam.Title,
                category = newExam.Category,
                level = newExam.Level,
                timeLimit = newExam.TimeLimit,
                totalScore = newExam.TotalScore,
                questions = questionsForMetadata,
                questionCountRequested = requestedCount ?? actualCount,
                questionCountActual = actualCount,
                questionCountAdjusted = requestedCount.HasValue && requestedCount.Value != actualCount,
                warning = requestedCount.HasValue && requestedCount.Value != actualCount
                    ? $"Đã tạo được {actualCount}/{requestedCount.Value} câu hỏi từ kho hiện có."
                    : null,
                intent = "create_exam"
            });
        }
        [HttpPost("upload-file")]
        [Authorize(Roles = "Admin")] // Chỉ Admin mới được tải file lên tạo đề
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadFile(
            IFormFile file,
            [FromServices] CloudinaryService cloudinary)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { message = "Vui lòng chọn tệp để tải lên." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".pdf" && ext != ".docx")
                return BadRequest(new { message = "Chỉ hỗ trợ tệp .pdf hoặc .docx." });

            if (file.Length > 20 * 1024 * 1024)
                return BadRequest(new { message = "Tệp quá lớn. Kích thước tối đa là 20MB." });

            try
            {
                // 1. Extract text (cho chatbot AI)
                string extractedText;
                using (var stream = file.OpenReadStream())
                {
                    if (ext == ".pdf")
                        extractedText = await _fileParserService.ExtractTextFromPdfAsync(stream);
                    else
                        extractedText = await _fileParserService.ExtractTextFromDocxAsync(stream);
                }

                // 2. Extract ảnh nhúng (song song, không fail nếu lỗi)
                var imageUrls = new List<string>();
                try
                {
                    QuizApi.Services.DocumentWithImages parsed;
                    using (var stream2 = file.OpenReadStream())
                    {
                        parsed = ext == ".pdf"
                            ? await _fileParserService.ExtractTextAndImagesFromPdfAsync(stream2)
                            : await _fileParserService.ExtractTextAndImagesFromDocxAsync(stream2);
                    }

                    // Upload từng ảnh lên Cloudinary
                    foreach (var (key, base64) in parsed.Images)
                    {
                        try
                        {
                            var publicId = $"chatbot_{DateTime.UtcNow.Ticks}_{key.ToLower()}";
                            var cdnUrl   = await cloudinary.UploadBase64Async(base64, "chatbot-images", publicId);
                            imageUrls.Add(cdnUrl);
                        }
                        catch { /* bỏ qua ảnh lỗi */ }
                    }
                }
                catch { /* không có ảnh hoặc không extract được — không sao */ }

                if (string.IsNullOrWhiteSpace(extractedText) && imageUrls.Count == 0)
                    return BadRequest(new { message = "Không thể trích xuất nội dung từ tệp này. Tệp có thể bị bảo vệ hoặc rỗng." });

                // Nếu PDF scan (không có text nhưng có ảnh), tạo text mô tả
                if (string.IsNullOrWhiteSpace(extractedText))
                    extractedText = $"[File có {imageUrls.Count} hình ảnh đính kèm, vui lòng dùng tính năng Import trong trang Ngân hàng câu hỏi để đọc ảnh chính xác hơn]";

                return Ok(new
                {
                    fileName  = file.FileName,
                    text      = extractedText,
                    charCount = extractedText.Length,
                    imageUrls // mảng URL Cloudinary
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UploadFile Error] {ex.Message}");
                return StatusCode(500, new { message = "Lỗi khi xử lý tệp tải lên." });
            }
        }


        [HttpDelete("history")]
        public async Task<IActionResult> ClearHistory()
        {
            try
            {
                string currentUsername = User.Identity?.Name ?? "Admin";
                var history = await _context.ChatMessages
                    .Where(m => m.Username == currentUsername)
                    .ToListAsync();

                if (history.Any())
                {
                    _context.ChatMessages.RemoveRange(history);
                    await _context.SaveChangesAsync();
                }

                return Ok(new { message = "Đã xóa dữ liệu trò chuyện thành công." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi xóa lịch sử trò chuyện.", error = ex.Message });
            }
        }
    }

    public class ChatRequest
    {
        public string? Message { get; set; }
        public string? FileContent { get; set; }
        public string? FileName { get; set; }
        public List<string>? ImageUrls { get; set; }  // URLs Cloudinary ảnh từ file
    }
}

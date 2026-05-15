using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace QuizApi.Services
{
    public class GeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;

        public GeminiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["GeminiSettings:ApiKey"]!;
            _model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? configuration["GeminiSettings:Model"]!;
        }

        // Hàm debug: Liệt kê tất cả các model được hỗ trợ
        public async Task<string> ListModelsAsync()
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> ChatAsync(string userMessage)
        {
            // Sử dụng v1beta cho gemini-1.5-flash
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = userMessage }
                        }
                    }
                }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Gemini Error] Status: {response.StatusCode}, Content: {errorContent}");
                throw new Exception($"Gemini API Error: {response.StatusCode} - {errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);
            
            // Trích xuất text từ response của Gemini
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "Không có phản hồi từ AI.";
        }

        public async Task<(string Category, int Count)> ParseCreateExamIntent(string userMessage)
        {
            // Prompt để AI trích xuất thông tin
            string systemPrompt = @"Bạn là trợ lý cho hệ thống thi trắc nghiệm. Nhiệm vụ của bạn là phân tích yêu cầu tạo đề thi của Admin. 
Trả về kết quả dưới dạng JSON duy nhất: { ""category"": ""tên_category"", ""count"": số_lượng }. 
Nếu không tìm thấy, để trống hoặc mặc định count = 10. 
Ví dụ: 'Tạo đề C# 20 câu' -> { ""category"": ""C#"", ""count"": 20 }";

            var fullPrompt = $"{systemPrompt}\n\nUser request: {userMessage}";
            var aiResponse = await ChatAsync(fullPrompt);

            try
            {
                // Làm sạch response để parse JSON
                int start = aiResponse.IndexOf('{');
                int end = aiResponse.LastIndexOf('}');
                if (start != -1 && end != -1)
                {
                    var jsonStr = aiResponse.Substring(start, end - start + 1);
                    var result = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr);
                    
                    string category = result?["category"]?.ToString() ?? "";
                    int count = 10;
                    if (result != null && result.ContainsKey("count"))
                    {
                        int.TryParse(result["count"].ToString(), out count);
                    }
                    return (category, count);
                }
            }
            catch { }

            return ("", 0);
        }
    }
}

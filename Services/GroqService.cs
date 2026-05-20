using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using QuizApi.Models;

namespace QuizApi.Services
{
    public class GroqService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;

        public GroqService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? configuration["GroqSettings:ApiKey"]!;
            _model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? configuration["GroqSettings:Model"] ?? "llama-3.3-70b-versatile";
            
            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new InvalidOperationException("GROQ_API_KEY environment variable hoặc GroqSettings:ApiKey trong appsettings.json chưa được cấu hình!");
            }
        }

        public async Task<string> ChatAsync(string userMessage)
        {
            var url = "https://api.groq.com/openai/v1/chat/completions";

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "user", content = userMessage }
                }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Groq API Error: {response.StatusCode} - {errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);
            
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "No response from Groq.";
        }

        public async Task<string> ChatAsync(string systemPrompt, List<ChatMessage> history, string userMessage)
        {
            var url = "https://api.groq.com/openai/v1/chat/completions";

            var messagesList = new List<object>
            {
                new { role = "system", content = systemPrompt }
            };

            foreach (var msg in history)
            {
                messagesList.Add(new { role = "user", content = msg.UserMessage });
                messagesList.Add(new { role = "assistant", content = msg.AiResponse });
            }

            messagesList.Add(new { role = "user", content = userMessage });

            var requestBody = new
            {
                model = _model,
                messages = messagesList.ToArray()
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Groq API Error: {response.StatusCode} - {errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);
            
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "No response from Groq.";
        }
    }
}

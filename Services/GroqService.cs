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

        private const string VisionModel = "qwen/qwen3.8-27b";

        private const string GroqApiUrl = "https://api.groq.com/openai/v1/chat/completions";

        public GroqService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? configuration["GroqSettings:ApiKey"]!;
            _model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? configuration["GroqSettings:Model"] ?? "qwen/qwen3.8-27b";

            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new InvalidOperationException("GROQ_API_KEY environment variable hoặc GroqSettings:ApiKey trong appsettings.json chưa được cấu hình!");
            }
        }

        public async Task<string> ChatAsync(string userMessage)
        {
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "user", content = userMessage }
                }
            };

            return await SendRequestAsync(requestBody);
        }

        public async Task<string> ChatAsync(string systemPrompt, List<ChatMessage> history, string userMessage)
        {
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

            return await SendRequestAsync(requestBody);
        }

        public async Task<string> ChatWithVisionAsync(string systemPrompt, List<string> base64JpegImages)
        {

            var contentItems = new List<object>();

            contentItems.Add(new
            {
                type = "text",
                text = systemPrompt
            });

            foreach (var base64 in base64JpegImages)
            {
                contentItems.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:image/jpeg;base64,{base64}"
                    }
                });
            }

            var requestBody = new
            {
                model = VisionModel,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = contentItems.ToArray()
                    }
                },

                max_tokens = 8000
            };

            return await SendRequestAsync(requestBody);
        }

        private async Task<string> SendRequestAsync(object requestBody)
        {
            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(GroqApiUrl, content);

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

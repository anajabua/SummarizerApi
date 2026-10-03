using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace SummarizerApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SummaryController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public SummaryController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    public async Task<IActionResult> Summarize([FromBody] SummaryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest(new { error = "The text is empty" });
        }

        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            return StatusCode(500, new { error = "API key is not specified" });
        }

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new
                        {
                            text = "Summarize the following text briefly, in the same language as it is written:\n\n" + request.Text
                        }
                    }
                }
            }
        };

        var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent"; var client = _httpClientFactory.CreateClient();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(502, new { error = "AI service error", details = responseText });
        }

        using var doc = JsonDocument.Parse(responseText);
        var summary = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return Ok(new SummaryResponse { Summary = summary });
    }
}

public class SummaryRequest
{
    public string Text { get; set; } = "";
}

public class SummaryResponse
{
    public string Summary { get; set; } = "";
}
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace SummarizerApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FileSummaryController : ControllerBase
{
    private const long MaxFileSize = 5 * 1024 * 1024; 
    private const int MaxTextLength = 30000;         

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public FileSummaryController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    public async Task<IActionResult> SummarizeFile(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is not selected" });

        if (file.Length > MaxFileSize)
            return BadRequest(new { error = "File is too large (maximum 5 MB)" });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        string text;
        try
        {
            using var stream = file.OpenReadStream();
            text = extension switch
            {
                ".pdf" => ReadPdf(stream),
                ".docx" => ReadDocx(stream),
                ".txt" => await new StreamReader(stream).ReadToEndAsync(),
                _ => throw new NotSupportedException()
            };
        }
        catch (NotSupportedException)
        {
            return BadRequest(new { error = "Only PDF, DOCX, and TXT are supported" });
        }
        catch (Exception)
        {
            return BadRequest(new { error = "Could not read file" });
        }

        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "No text found in file (may be a scanned image)" });

        if (text.Length > MaxTextLength)
            text = text.Substring(0, MaxTextLength);

        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new { error = "API key is not provided" });

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
                            text = "Summarize the following document briefly, in the same language as it is written:\n\n" + text
                        }
                    }
                }
            }
        };

        var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent";

        var client = _httpClientFactory.CreateClient();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { error = "AI service error", details = responseText });

        using var doc = JsonDocument.Parse(responseText);
        var summary = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return Ok(new { fileName = file.FileName, summary });
    }

    private static string ReadPdf(Stream stream)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(stream);
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private static string ReadDocx(Stream stream)
    {
        using var wordDoc = WordprocessingDocument.Open(stream, false);
        return wordDoc.MainDocumentPart?.Document.Body?.InnerText ?? "";
    }
}
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
namespace SummarizerApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VideoSummaryController : ControllerBase
{
    private const long MaxVideoSize = 200 * 1024 * 1024; 
    private const string BaseUrl = "https://generativelanguage.googleapis.com";
    private const string Model = "gemini-3.5-flash-lite";

    private static readonly HashSet<string> AllowedTypes = new()
    {
        "video/mp4", "video/mpeg", "video/quicktime", "video/webm", "video/x-msvideo"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public VideoSummaryController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> SummarizeVideo(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file selected" });

        if (file.Length > MaxVideoSize)
            return BadRequest(new { error = "Video is too large (maximum 200 MB)" });

        if (!AllowedTypes.Contains(file.ContentType))
            return BadRequest(new { error = "Only MP4, MPEG, MOV, WEBM, AVI formats are supported" });

        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new { error = "API key is not specified" });

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);

        var startReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/upload/v1beta/files");
        startReq.Headers.Add("x-goog-api-key", apiKey);
        startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startReq.Headers.Add("X-Goog-Upload-Command", "start");
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", file.Length.ToString());
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", file.ContentType);
        startReq.Content = new StringContent(
            JsonSerializer.Serialize(new { file = new { display_name = file.FileName } }),
            Encoding.UTF8, "application/json");

        var startRes = await client.SendAsync(startReq);
        if (!startRes.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Failed to start upload", details = await startRes.Content.ReadAsStringAsync() });

        if (!startRes.Headers.TryGetValues("X-Goog-Upload-URL", out var urlValues))
            return StatusCode(502, new { error = "Failed to get upload URL" });
        var uploadUrl = urlValues.First();

        await using var stream = file.OpenReadStream();
        var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadReq.Content = new StreamContent(stream);
        uploadReq.Content.Headers.ContentLength = file.Length;

        var uploadRes = await client.SendAsync(uploadReq);
        var uploadText = await uploadRes.Content.ReadAsStringAsync();
        if (!uploadRes.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Failed to upload video", details = uploadText });

        using var uploadDoc = JsonDocument.Parse(uploadText);
        var fileInfo = uploadDoc.RootElement.GetProperty("file");
        var fileUri = fileInfo.GetProperty("uri").GetString()!;
        var fileName = fileInfo.GetProperty("name").GetString()!;

        var ready = false;
        for (var i = 0; i < 60; i++)
        {
            var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1beta/{fileName}");
            statusReq.Headers.Add("x-goog-api-key", apiKey);
            var statusRes = await client.SendAsync(statusReq);
            using var statusDoc = JsonDocument.Parse(await statusRes.Content.ReadAsStringAsync());
            var state = statusDoc.RootElement.GetProperty("state").GetString();

            if (state == "ACTIVE") { ready = true; break; }
            if (state == "FAILED")
                return StatusCode(502, new { error = "Google failed to process the video" });

            await Task.Delay(5000);
        }

        if (!ready)
            return StatusCode(504, new { error = "Video processing took too long" });

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { file_data = new { mime_type = file.ContentType, file_uri = fileUri } },
                        new { text = "Briefly summarize this video in the same language spoken in it. List the main points." }
                    }
                }
            }
        };
        var bodyJson = JsonSerializer.Serialize(body);

        const int maxAttempts = 4;
        string genText = "";
        var success = false;
        var lastStatus = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var genReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1beta/models/{Model}:generateContent");
            genReq.Headers.Add("x-goog-api-key", apiKey);
            genReq.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var genRes = await client.SendAsync(genReq);
            genText = await genRes.Content.ReadAsStringAsync();
            lastStatus = (int)genRes.StatusCode;

            if (genRes.IsSuccessStatusCode)
            {
                success = true;
                break;
            }

            var retryable = lastStatus == 503 || lastStatus == 429 || lastStatus == 500;
            if (!retryable || attempt == maxAttempts)
                break;

            await Task.Delay(TimeSpan.FromSeconds(5 * Math.Pow(2, attempt - 1)));
        }

        if (!success)
        {
            var message = lastStatus switch
            {
                503 => "Google's server is overloaded, try again in a few minutes",
                429 => "Free limit exceeded, try again later or with a shorter video",
                _ => "AI service error"
            };
            return StatusCode(502, new { error = message, details = genText });
        }

        using var genDoc = JsonDocument.Parse(genText);
        var summary = genDoc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return Ok(new { fileName = file.FileName, summary });
    }
}

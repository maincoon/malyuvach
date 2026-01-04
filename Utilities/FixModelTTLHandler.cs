using System.Text;
using System.Text.Json;

namespace Malyuvach.Services.Utilities;

/// <summary>
/// Ugly hack to add keep_alive: 0 to the request JSON payload
/// </summary>
public class FixModelTTLHandler : DelegatingHandler
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly int? _keepAlive;

    public FixModelTTLHandler(JsonSerializerOptions jsonOptions, int? keepAlive = 0)
    {
        _jsonOptions = jsonOptions;
        _keepAlive = keepAlive;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var jsonText = await request.Content.ReadAsStringAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonText, _jsonOptions)!;
                root["keep_alive"] = _keepAlive;
                var updatedJson = JsonSerializer.Serialize(root, _jsonOptions);
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content = new StringContent(updatedJson, Encoding.UTF8, mediaType);
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}


using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.AI;

/// <summary>
/// Client for the local Ollama inference service.
/// Enforces local-only loopback URLs and strict response bounding.
/// </summary>
public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<OllamaClient> _logger;

    public const int MaxResponseSizeBytes = 5 * 1024 * 1024; // 5 MB ceiling

    public OllamaClient(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<OllamaClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private void ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Ollama endpoint URL cannot be empty.", nameof(endpoint));
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL format: '{endpoint}'", nameof(endpoint));
        }

        var config = _configurationService.Current;
        if (config.AI.RequireLocalEndpoint)
        {
            if (!uri.IsLoopback && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Remote AI endpoints are strictly prohibited for privacy safety. Host '{uri.Host}' is not a local loopback address (localhost / 127.0.0.1).");
            }
        }
    }

    public async Task<AiConnectionStatus> CheckConnectionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        var sw = Stopwatch.StartNew();

        try
        {
            var baseUri = endpoint.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUri}/api/tags");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new AiConnectionStatus
                {
                    IsAvailable = false,
                    Endpoint = endpoint,
                    ResponseTimeMs = sw.ElapsedMilliseconds,
                    ErrorMessage = $"Ollama returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = ParseModelsFromJson(json);

            return new AiConnectionStatus
            {
                IsAvailable = true,
                Endpoint = endpoint,
                AvailableModels = models,
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiConnectionStatus
            {
                IsAvailable = false,
                Endpoint = endpoint,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Connection timed out connecting to local Ollama service (10s limit exceeded)."
            };
        }
        catch (HttpRequestException ex)
        {
            return new AiConnectionStatus
            {
                IsAvailable = false,
                Endpoint = endpoint,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ErrorMessage = $"Local Ollama service unreachable at {endpoint}. Please verify Ollama is running. ({ex.Message})"
            };
        }
        catch (Exception ex)
        {
            return new AiConnectionStatus
            {
                IsAvailable = false,
                Endpoint = endpoint,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ErrorMessage = $"Connection check failed: {ex.Message}"
            };
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);

        var baseUri = endpoint.TrimEnd('/');
        using var response = await _httpClient.GetAsync($"{baseUri}/api/tags", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseModelsFromJson(json);
    }

    public async Task<string> GenerateJsonAsync(
        string endpoint,
        string model,
        string systemPrompt,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var config = _configurationService.Current;
        var timeoutSeconds = config.AI.TimeoutSeconds > 0 ? config.AI.TimeoutSeconds : 60;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var requestBody = new
        {
            model = model,
            prompt = prompt,
            system = systemPrompt,
            stream = false,
            format = "json",
            options = new
            {
                temperature = 0.1
            }
        };

        var baseUri = endpoint.TrimEnd('/');
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending inference request to Ollama model {Model} at {Endpoint} (Timeout: {Timeout}s)", model, endpoint, timeoutSeconds);

        using var response = await _httpClient.PostAsync($"{baseUri}/api/generate", content, linkedCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            throw new HttpRequestException($"Ollama inference failed with HTTP {(int)response.StatusCode}: {errText}");
        }

        var responseString = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
        if (responseString.Length > MaxResponseSizeBytes)
        {
            throw new InvalidOperationException($"Ollama response exceeded maximum allowable buffer of {MaxResponseSizeBytes} bytes ({responseString.Length} bytes received).");
        }

        using var doc = JsonDocument.Parse(responseString);
        if (!doc.RootElement.TryGetProperty("response", out var responseProperty))
        {
            throw new FormatException("Ollama response missing 'response' payload property.");
        }

        return responseProperty.GetString() ?? string.Empty;
    }

    public async Task<string> SendChatAsync(
        string endpoint,
        string model,
        string systemPrompt,
        IEnumerable<ChatMessageRecord> history,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(history);

        var config = _configurationService.Current;
        var timeoutSeconds = config.AI.TimeoutSeconds > 0 ? config.AI.TimeoutSeconds : 60;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var messagesList = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messagesList.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var msg in history)
        {
            messagesList.Add(new { role = msg.Role, content = msg.Content });
        }

        var requestBody = new
        {
            model = model,
            messages = messagesList,
            stream = false,
            options = new
            {
                temperature = 0.3
            }
        };

        var baseUri = endpoint.TrimEnd('/');
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending chat turn to Ollama model {Model} at {Endpoint} (Messages: {Count})",
            model, endpoint, messagesList.Count);

        using var response = await _httpClient.PostAsync($"{baseUri}/api/chat", content, linkedCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            throw new HttpRequestException($"Ollama chat failed with HTTP {(int)response.StatusCode}: {errText}");
        }

        var responseString = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
        if (responseString.Length > MaxResponseSizeBytes)
        {
            throw new InvalidOperationException($"Ollama chat response exceeded maximum allowable buffer of {MaxResponseSizeBytes} bytes.");
        }

        using var doc = JsonDocument.Parse(responseString);
        if (doc.RootElement.TryGetProperty("message", out var messageProp) &&
            messageProp.TryGetProperty("content", out var contentProp))
        {
            return contentProp.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ParseModelsFromJson(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in modelsArray.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(name);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback empty
        }
        return result;
    }
}

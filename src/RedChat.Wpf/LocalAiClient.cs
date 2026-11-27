using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RedChat.Wpf;

internal static class LocalAiClient
{
    private const string Model = "llama3.2:1b";
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static async Task<string> CompleteAsync(
        IReadOnlyList<(string Role, string Content)> history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);

        List<object> messages = new()
        {
            new
            {
                role = "system",
                content = "Tu es un assistant utile intégré à RedChat. Réponds en français par défaut, clairement et utilement. Tu fonctionnes localement et tu n'as pas accès aux conversations RedChat, aux fichiers ni à Internet.",
            },
        };
        messages.AddRange(history.Select(item => (object)new { role = item.Role, content = item.Content }));

        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = Model,
            messages,
            stream = false,
        });

        using HttpRequestMessage request = new(HttpMethod.Post, "api/chat")
        {
            Content = new ByteArrayContent(requestBytes),
        };
        request.Content.Headers.ContentType = new("application/json");

        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        byte[] responseBytes = await ReadResponseBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(responseBytes);

        if (!response.IsSuccessStatusCode)
        {
            string detail = document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? string.Empty
                    : response.ReasonPhrase ?? "Erreur inconnue";
            if (detail.Length > 500)
            {
                detail = detail[..500];
            }

            string hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? " Installe le modèle avec la commande : ollama run llama3.2:1b."
                : string.Empty;
            throw new HttpRequestException(
                $"Ollama a répondu {(int)response.StatusCode} ({response.StatusCode}) : {detail}.{hint}",
                inner: null,
                response.StatusCode);
        }

        if (!document.RootElement.TryGetProperty("message", out JsonElement assistantMessage)
            || !assistantMessage.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new InvalidDataException("La réponse d'Ollama ne contient pas de texte exploitable.");
        }

        return content.GetString()!;
    }

    private static async Task<byte[]> ReadResponseBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maxResponseBytes = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maxResponseBytes)
        {
            throw new InvalidDataException("La réponse locale dépasse la taille maximale autorisée.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > maxResponseBytes)
            {
                throw new InvalidDataException("La réponse locale dépasse la taille maximale autorisée.");
            }

            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }
}

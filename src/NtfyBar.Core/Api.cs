using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace NtfyBar.Core;

public sealed record TokenResponse
{
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("expires")] public long? Expires { get; init; }
}

public sealed class SignInException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

public static class NtfyApi
{
    /// <summary>Token label per device: <c>ntfy-bar-win-&lt;machine&gt;</c> (spec §9.3).</summary>
    public static string TokenLabel(string machineName)
    {
        var clean = new string((machineName ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (clean.Length == 0) clean = "pc";
        return TextUtil.Truncate($"ntfy-bar-win-{clean}", 64).TrimEnd('…');
    }

    /// <summary><c>POST /v1/account/token</c> with Basic auth → <c>tk_…</c> (spec §14.4).</summary>
    public static async Task<string> MintTokenAsync(HttpClient http, string baseUrl, string username, string password, string label, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/account/token");
        req.Headers.TryAddWithoutValidation("Authorization", StreamRules.BasicAuth(username, password));
        req.Content = new StringContent(Json.Write(new Dictionary<string, string> { ["label"] = label }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new SignInException($"Could not reach the server: {e.InnerException?.Message ?? e.Message}"); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SignInException("Wrong username or password.", (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                throw new SignInException($"Server returned HTTP {(int)resp.StatusCode}.", (int)resp.StatusCode);
            var token = Json.Parse<TokenResponse>(body)?.Token;
            if (string.IsNullOrEmpty(token) || !token.StartsWith("tk_", StringComparison.Ordinal))
                throw new SignInException("Server did not return an access token.", (int)resp.StatusCode);
            return token;
        }
    }
}

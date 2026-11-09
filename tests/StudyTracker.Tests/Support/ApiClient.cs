using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StudyTracker.Tests.Support;

/// <summary>Ответ API: тело пустое, если сервер ничего не вернул (тогда <c>Body.ValueKind == Undefined</c>).</summary>
public sealed record ApiResponse(int Status, JsonElement Body, HttpResponseHeaders Headers);

/// <summary>Ответ обычной страницы или файла (не API).</summary>
public sealed record PageResponse(int Status, string Text, HttpResponseHeaders Headers);

/// <summary>Браузерный сеанс: свои cookie и свой CSRF-токен, как <c>Client</c> в <c>tests/support.py</c>.</summary>
public sealed class ApiClient(HttpClient http)
{
    public const string DefaultPassword = "a-long-test-password";

    public string? Token { get; set; }

    /// <summary>Заголовки, которые уходят с каждым запросом (например, <c>Cookie</c> другого сеанса).</summary>
    public HttpRequestHeaders DefaultHeaders => http.DefaultRequestHeaders;

    /// <summary>Все запросы клиента уходят с <c>Authorization: Bearer …</c>.</summary>
    public void UseBearer(string token) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, bool csrf = true)
    {
        if (csrf && Token is null && path != "token") await RefreshToken();
        using var request = new HttpRequestMessage(method, "/api/" + path);
        if (csrf && Token is not null) request.Headers.Add("X-CSRF-TOKEN", Token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new ApiResponse((int)response.StatusCode, json, response.Headers);
    }

    public async Task<PageResponse> Page(string path)
    {
        using var response = await http.GetAsync(path);
        return new PageResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers);
    }

    /// <summary>Регистрирует аккаунт и входит в него; любой другой ответ — ошибка теста.</summary>
    public async Task SignIn(string email = "api@example.test", string password = DefaultPassword)
    {
        var registered = await Register(email, password);
        Assert.True(registered.Status == 201, "Регистрация: " + registered.Status);
        var login = await Login(email, password);
        Assert.True(login.Status == 200, "Вход: " + login.Status);
    }

    public Task<ApiResponse> Get(string path) => Send(HttpMethod.Get, path);
    public Task<ApiResponse> Post(string path, object? body = null, bool csrf = true) => Send(HttpMethod.Post, path, body, csrf);
    public Task<ApiResponse> Put(string path, object? body = null, bool csrf = true) => Send(HttpMethod.Put, path, body, csrf);
    public Task<ApiResponse> Delete(string path, bool csrf = true) => Send(HttpMethod.Delete, path, null, csrf);

    public async Task RefreshToken() =>
        Token = (await Send(HttpMethod.Get, "token", csrf: false)).Body.GetProperty("token").GetString();

    public Task<ApiResponse> Register(string email, string password = DefaultPassword) =>
        Post("auth/register", new { email, password });

    public async Task<ApiResponse> Login(string email, string password = DefaultPassword)
    {
        var result = await Post("auth/login", new { email, password });
        await RefreshToken();
        return result;
    }

    public async Task<ApiResponse> Logout()
    {
        var result = await Post("auth/logout");
        await RefreshToken();
        return result;
    }
}

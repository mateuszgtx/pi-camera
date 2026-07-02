using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace pi_camera;

public static partial class Program
{
    private const string WebAuthCookieName = "piCameraAuth";
    private const int WebPasswordIterations = 120_000;
    private static readonly object _webAuthLock = new();
    private static readonly Dictionary<string, DateTime> _webSessions = new(StringComparer.Ordinal);
    private static string _webPasswordHash = "";

    private static bool IsWebPasswordEnabled()
    {
        lock (_webAuthLock)
            return !string.IsNullOrWhiteSpace(_webPasswordHash);
    }

    private static object CurrentWebAuthStatus(HttpContext context)
    {
        var enabled = IsWebPasswordEnabled();
        return new
        {
            enabled,
            authenticated = !enabled || IsRequestAuthenticated(context),
            cookieName = WebAuthCookieName
        };
    }

    private static bool RequiresWebAuthentication(PathString path)
    {
        if (!path.StartsWithSegments("/api"))
            return false;

        var value = path.Value ?? "";
        return !value.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnforceOptionalWebAuthenticationAsync(HttpContext context, Func<Task> next)
    {
        if (!IsWebPasswordEnabled() || !RequiresWebAuthentication(context.Request.Path) || IsRequestAuthenticated(context))
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            authRequired = true,
            message = "Password required"
        });
    }

    private static async Task<IResult> LoginWebAsync(HttpContext context)
    {
        if (!IsWebPasswordEnabled())
            return Results.Ok(new { ok = true, enabled = false, authenticated = true });

        string password;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            password = ReadPasswordField(doc.RootElement);
        }
        catch
        {
            return Results.BadRequest(new { ok = false, message = "Invalid login request" });
        }

        if (!VerifyWebPassword(password))
            return Results.Unauthorized();

        CreateWebSession(context);
        return Results.Ok(new { ok = true, enabled = true, authenticated = true });
    }

    private static IResult LogoutWeb(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(WebAuthCookieName, out var token))
        {
            lock (_webAuthLock)
                _webSessions.Remove(token);
        }

        context.Response.Cookies.Delete(WebAuthCookieName);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> SetWebPasswordAsync(HttpContext context)
    {
        string password;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            password = ReadPasswordField(doc.RootElement);
        }
        catch
        {
            return Results.BadRequest(new { ok = false, message = "Invalid password request" });
        }

        password = password.Trim();
        if (password.Length < 4)
            return Results.BadRequest(new { ok = false, message = "Password must have at least 4 characters" });

        SetWebPassword(password);
        CreateWebSession(context);
        SavePersistentSettingsToDisk();

        return Results.Ok(new { ok = true, enabled = true, authenticated = true });
    }

    private static IResult ClearWebPasswordFromApi(HttpContext context)
    {
        ClearWebPassword("web panel");
        context.Response.Cookies.Delete(WebAuthCookieName);
        return Results.Ok(new { ok = true, enabled = false, authenticated = true });
    }

    private static string ReadPasswordField(JsonElement root)
    {
        if (TryGetString(root, "password", out var password))
            return password;
        if (TryGetString(root, "newPassword", out var newPassword))
            return newPassword;
        return "";
    }

    private static bool IsRequestAuthenticated(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(WebAuthCookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return false;

        lock (_webAuthLock)
        {
            var now = DateTime.UtcNow;
            PurgeExpiredWebSessions(now);

            if (!_webSessions.TryGetValue(token, out var expiresUtc) || expiresUtc <= now)
            {
                _webSessions.Remove(token);
                return false;
            }

            _webSessions[token] = now.AddDays(30);
            return true;
        }
    }

    private static void CreateWebSession(HttpContext context)
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresUtc = DateTime.UtcNow.AddDays(30);

        lock (_webAuthLock)
        {
            PurgeExpiredWebSessions(DateTime.UtcNow);
            _webSessions[token] = expiresUtc;
        }

        context.Response.Cookies.Append(WebAuthCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = expiresUtc
        });
    }

    private static void PurgeExpiredWebSessions(DateTime now)
    {
        if (_webSessions.Count == 0)
            return;

        foreach (var expired in _webSessions.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _webSessions.Remove(expired);
    }

    private static void SetWebPassword(string password)
    {
        lock (_webAuthLock)
        {
            _webPasswordHash = CreateWebPasswordHash(password);
            _webSessions.Clear();
        }

        Console.WriteLine("[AUTH] web password set");
    }

    private static void SetWebPasswordHashFromDisk(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || !hash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal))
            return;

        lock (_webAuthLock)
        {
            _webPasswordHash = hash;
            _webSessions.Clear();
        }
    }

    private static void ClearWebPassword(string reason)
    {
        lock (_webAuthLock)
        {
            _webPasswordHash = "";
            _webSessions.Clear();
        }

        SavePersistentSettingsToDisk();
        SetNetworkStatus("Web password reset");
        Console.WriteLine($"[AUTH] web password cleared ({reason})");
    }

    private static string CurrentWebPasswordHashForSnapshot()
    {
        lock (_webAuthLock)
            return _webPasswordHash;
    }

    private static bool VerifyWebPassword(string password)
    {
        string hash;
        lock (_webAuthLock)
            hash = _webPasswordHash;

        if (string.IsNullOrWhiteSpace(hash))
            return true;

        try
        {
            var parts = hash.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
                return false;

            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string CreateWebPasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            WebPasswordIterations,
            HashAlgorithmName.SHA256,
            32);

        return $"pbkdf2-sha256${WebPasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

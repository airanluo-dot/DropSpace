using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DropSpace.Infrastructure.Lyrics;

public enum QqMusicSessionState { SignedOut, Saved, Connected, Expired, Rejected, StorageError }

// Deliberately not a record: generated ToString must never include credential values.
public sealed class QqMusicCookie
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Path { get; init; } = "/";
    public double? Expires { get; init; }
    public bool HttpOnly { get; init; }
}

/// <summary>Only the session explicitly supplied by DropSpace's own QQ Music login window.</summary>
public sealed class QqMusicSession(string directory)
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
        { "qqmusic_key", "qqmusic_uin", "qm_keyst", "uin", "wxuin", "login_type" };
    private readonly SemaphoreSlim _storage = new(1, 1);
    private readonly object _gate = new();
    private QqMusicCookie[] _cookies = [];
    private bool _loaded;
    private QqMusicSessionState _state;
    private long _generation;
    private DateTimeOffset _rateLimitUntil;
    public string BrowserDirectory => System.IO.Path.Combine(directory, "Browser");
    private string SessionPath => System.IO.Path.Combine(directory, "session.bin");
    public event EventHandler? Changed;
    public event EventHandler? CredentialsChanged;
    public QqMusicSessionState State { get { lock (_gate) return _state; } }
    public bool HasSavedSession { get { lock (_gate) return _cookies.Length > 0 || _state == QqMusicSessionState.StorageError; } }
    public long Generation { get { lock (_gate) return _generation; } }

    public async Task LoadAsync(CancellationToken token = default)
    {
        await _storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            if (File.Exists(SessionPath))
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                if (new FileInfo(SessionPath).Length > 64 * 1024) throw new InvalidDataException();
                var encrypted = await File.ReadAllBytesAsync(SessionPath, token).ConfigureAwait(false);
                var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try
                {
                    var cookies = Validate(JsonSerializer.Deserialize<QqMusicCookie[]>(plain) ?? []);
                    lock (_gate) { _cookies = cookies; _state = HasTicket(cookies) ? QqMusicSessionState.Saved : QqMusicSessionState.Expired; }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            _loaded = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        { lock (_gate) _state = QqMusicSessionState.StorageError; _loaded = true; }
        finally { _storage.Release(); }
        Notify(Changed);
    }

    public async Task SaveAsync(IEnumerable<QqMusicCookie> cookies, CancellationToken token = default)
    {
        var accepted = Validate(cookies);
        if (!HasTicket(accepted)) throw new InvalidDataException("QQ Music has not supplied a usable session.");
        await _storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAsync(accepted, token).ConfigureAwait(false);
            lock (_gate) { _cookies = accepted; _state = QqMusicSessionState.Saved; _generation++; _loaded = true; }
        }
        finally { _storage.Release(); }
        Notify(Changed); Notify(CredentialsChanged);
    }

    public async Task SignOutAsync(CancellationToken token = default)
    {
        await _storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Persist removal before reporting success; an I/O failure leaves the old visible state.
            File.Delete(SessionPath);
            lock (_gate) { _cookies = []; _state = QqMusicSessionState.SignedOut; _generation++; _loaded = true; }
        }
        finally { _storage.Release(); }
        Notify(Changed); Notify(CredentialsChanged);
    }

    public QqMusicCookie[] GetBrowserCookies() { lock (_gate) return _cookies.Where(IsCurrent).ToArray(); }
    public static bool HasUsableSession(IEnumerable<QqMusicCookie> cookies) => HasTicket(Validate(cookies));
    public void AllowExplicitRetry()
    {
        lock (_gate) if (HasTicket(_cookies)) _state = QqMusicSessionState.Saved;
        Notify(Changed);
    }

    internal void EnsureUsable()
    {
        bool blocked;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _rateLimitUntil)
                throw new LyricsProviderRejectedException("QQ Music request cooldown is active.", 429);
            if (_cookies.Length > 0 && !HasTicket(_cookies)) _state = QqMusicSessionState.Expired;
            blocked = _state is QqMusicSessionState.Expired or QqMusicSessionState.Rejected;
        }
        if (!blocked) return;
        Notify(Changed);
        throw new LyricsProviderRejectedException("QQ Music session needs attention.", 2001);
    }

    internal (string Uin, int Gtk) Identity()
    {
        lock (_gate)
        {
            var valid = _cookies.Where(IsCurrent).ToArray();
            string Get(string name) => valid.FirstOrDefault(cookie => cookie.Name == name)?.Value ?? "";
            var user = Get("qqmusic_uin");
            if (user.Length == 0) user = Get("uin");
            if (user.Length == 0) user = Get("wxuin");
            user = user.TrimStart('o');
            if (user.Length is < 1 or > 20 || !user.All(char.IsAsciiDigit)) user = "0";
            var key = Get("qqmusic_key");
            var hash = 5381;
            foreach (var character in key) hash = unchecked(hash + (hash << 5) + character);
            return (user, hash & 0x7fffffff);
        }
    }

    internal string CookieHeader(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Host is not ("u.y.qq.com" or "c.y.qq.com")) return "";
        lock (_gate)
        {
            var jar = new CookieContainer();
            foreach (var cookie in _cookies.Where(cookie => IsCurrent(cookie) &&
                (cookie.Domain.StartsWith('.') ? uri.Host.EndsWith(cookie.Domain, StringComparison.OrdinalIgnoreCase) :
                    string.Equals(uri.Host, cookie.Domain, StringComparison.OrdinalIgnoreCase))))
                jar.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain) { Secure = true });
            return jar.GetCookieHeader(uri);
        }
    }

    internal void ReportAccess(long generation, bool accepted, int? code = null)
    {
        lock (_gate)
        {
            if (_generation != generation) return;
            if (!accepted && code == 429)
            {
                _rateLimitUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                return;
            }
            if (!HasTicket(_cookies) || _state == QqMusicSessionState.StorageError) return;
            if (accepted) _state = QqMusicSessionState.Connected;
            else if (code is 1000 or 2001 or 101010 or 401) _state = QqMusicSessionState.Expired;
            // Other business/request refusals do not prove credential expiry and
            // must not block every subsequent song for the lifetime of the session.
        }
        Notify(Changed);
    }

    // A server-issued update can renew the local session. Never invent/extend its expiry.
    internal async Task ApplyResponseCookiesAsync(Uri uri, IEnumerable<string> headers, long generation, CancellationToken token)
    {
        await _storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var jar = new CookieContainer();
            lock (_gate)
            {
                if (_generation != generation || _cookies.Length == 0) return;
                foreach (var saved in _cookies.Where(IsCurrent))
                    jar.Add(new Cookie(saved.Name, saved.Value, saved.Path, saved.Domain) { Secure = true, HttpOnly = saved.HttpOnly,
                        Expires = saved.Expires is { } expiry ? DateTimeOffset.FromUnixTimeSeconds((long)expiry).UtcDateTime : DateTime.MinValue });
            }
            var changed = false;
            foreach (var header in headers.Take(16))
            {
                var separator = header.IndexOf('=');
                if (header.Length > 8192 || separator <= 0 || !Names.Contains(header[..separator].Trim())) continue;
                try { jar.SetCookies(uri, header); changed = true; } catch (CookieException) { }
            }
            if (!changed) return;
            // Seeding the jar also preserves explicit server deletions/expired cookies.
            var merged = Validate(jar.GetAllCookies().Cast<Cookie>().Select(cookie => new QqMusicCookie
            {
                Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path, HttpOnly = cookie.HttpOnly,
                Expires = cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds(),
            }));
            await WriteAsync(merged, token).ConfigureAwait(false);
            lock (_gate) { _cookies = merged; if (!HasTicket(merged)) _state = QqMusicSessionState.Expired; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { lock (_gate) _state = QqMusicSessionState.StorageError; }
        finally { _storage.Release(); }
        Notify(Changed);
    }

    private async Task WriteAsync(QqMusicCookie[] cookies, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(cookies);
        var temporary = SessionPath + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, encrypted, token).ConfigureAwait(false);
            File.Move(temporary, SessionPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static QqMusicCookie[] Validate(IEnumerable<QqMusicCookie> cookies) => cookies.Take(64).Where(cookie =>
        cookie is not null && cookie.Name is not null && cookie.Domain is not null && cookie.Value is not null &&
        Names.Contains(cookie.Name) && (cookie.Domain.TrimStart('.') is "qq.com" or "y.qq.com" or "c.y.qq.com" or "u.y.qq.com") &&
        cookie.Path == "/" && cookie.Value.Length is > 0 and <= 4096 &&
        cookie.Value.All(character => character is >= '!' and <= '~' && character is not ';' and not ',' and not '"' and not '\\') &&
        (cookie.Expires is null || double.IsFinite(cookie.Expires.Value) && cookie.Expires is >= 0 and <= 253402300799))
        .GroupBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).Select(group => group.Last()).ToArray();
    private static bool IsCurrent(QqMusicCookie cookie) => cookie.Expires is null || cookie.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static bool HasTicket(IEnumerable<QqMusicCookie> cookies) => cookies.Any(cookie =>
        (cookie.Name is "qqmusic_key" or "qm_keyst") && IsCurrent(cookie));
    private void Notify(EventHandler? handler) { try { handler?.Invoke(this, EventArgs.Empty); } catch (Exception error) when (error is not OutOfMemoryException) { } }
}

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Serilog;

/// <summary>
/// Điều tiết request tới nhatot.com dùng chung cho mọi job (HN + HCM).
/// nhatot chặn theo IP: gửi quá dày sẽ bị trả "Please try again later!" cho mọi trang,
/// kể cả khi mở bằng Chrome thường. Vì vậy:
///   - Chỉ 1 request tại một thời điểm, cách nhau ngẫu nhiên MinDelay..MaxDelay giây.
///   - Không quá MaxPerHour request trong 60 phút gần nhất.
///   - Khi bị chặn: mọi job cùng nghỉ, thời gian nghỉ tăng dần theo CooldownMinutes.
///     Hết nghỉ thì request đầu tiên đi qua (chỉ 1 do _gate) chính là request thử;
///     vẫn bị chặn thì nghỉ mức tiếp theo.
/// Các thông số đọc từ App.config (xem các key Nhatot* trong appSettings).
/// </summary>
public static class NhatotRateLimiter
{
    static readonly int MinDelaySeconds = GetInt("NhatotMinDelaySeconds", 30);
    static readonly int MaxDelaySeconds = Math.Max(MinDelaySeconds, GetInt("NhatotMaxDelaySeconds", 60));
    static readonly int MaxPerHour = GetInt("NhatotMaxPerHour", 60);
    static readonly int[] CooldownMinutes = GetIntList("NhatotCooldownMinutes", new[] { 60, 120, 240 });

    static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    static readonly Random _random = new Random();
    static readonly object _lock = new object();
    static readonly Queue<DateTime> _recentRequests = new Queue<DateTime>();

    static DateTime _blockedUntil = DateTime.MinValue;
    static DateTime _lastRequestAt = DateTime.MinValue;
    static int _blockCount = 0;

    /// <summary>Chạy <paramref name="action"/> sau khi đã chờ hết thời gian nghỉ / giãn cách / giới hạn giờ.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            await WaitIfBlockedAsync();
            await WaitHourlyLimitAsync();

            TimeSpan wait;
            lock (_lock)
            {
                var delay = TimeSpan.FromSeconds(_random.Next(MinDelaySeconds, MaxDelaySeconds + 1));
                wait = _lastRequestAt + delay - DateTime.Now;
            }
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            try
            {
                return await action();
            }
            finally
            {
                lock (_lock)
                {
                    _lastRequestAt = DateTime.Now;
                    _recentRequests.Enqueue(_lastRequestAt);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task WaitIfBlockedAsync()
    {
        TimeSpan remain;
        lock (_lock) remain = _blockedUntil - DateTime.Now;

        if (remain > TimeSpan.Zero)
        {
            Log.Information($"[NHATOT] Đang bị chặn, nghỉ thêm {remain.TotalMinutes:0.#} phút (đến {DateTime.Now + remain:HH:mm:ss}).");
            await Task.Delay(remain);
        }
    }

    static async Task WaitHourlyLimitAsync()
    {
        TimeSpan wait = TimeSpan.Zero;
        lock (_lock)
        {
            var hourAgo = DateTime.Now.AddHours(-1);
            while (_recentRequests.Count > 0 && _recentRequests.Peek() < hourAgo)
                _recentRequests.Dequeue();

            if (_recentRequests.Count >= MaxPerHour)
                wait = _recentRequests.Peek().AddHours(1) - DateTime.Now;
        }

        if (wait > TimeSpan.Zero)
        {
            Log.Information($"[NHATOT] Đã đủ {MaxPerHour} request/giờ, chờ {wait.TotalMinutes:0.#} phút (đến {DateTime.Now + wait:HH:mm:ss}).");
            await Task.Delay(wait);
        }
    }

    /// <summary>Gọi khi phát hiện trang trả về "Please try again later!".</summary>
    public static void ReportBlocked(string url)
    {
        lock (_lock)
        {
            // Nhiều request cùng báo trong lúc đang nghỉ thì chỉ tính 1 lần
            if (_blockedUntil > DateTime.Now) return;

            int minutes = CooldownMinutes[Math.Min(_blockCount, CooldownMinutes.Length - 1)];
            _blockCount++;
            _blockedUntil = DateTime.Now.AddMinutes(minutes);
            Log.Warning($"[NHATOT] Bị chặn IP (lần {_blockCount}) tại {url}. Tạm dừng {minutes} phút đến {_blockedUntil:HH:mm:ss}.");
        }
    }

    /// <summary>Gọi khi load trang thành công, reset mức nghỉ về ban đầu.</summary>
    public static void ReportSuccess()
    {
        lock (_lock) _blockCount = 0;
    }

    /// <summary>
    /// Kiểm tra trang có phải trang chặn của nhatot không:
    /// status 429/403, hoặc body chỉ có dòng "Please try again later!".
    /// </summary>
    public static async Task<bool> IsBlockedAsync(IPage page, IResponse response)
    {
        if (response != null && (response.Status == 429 || response.Status == 403))
            return true;

        try
        {
            var text = await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''");
            return text != null
                && text.Length < 200
                && text.IndexOf("try again later", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static int GetInt(string key, int fallback)
    {
        int value;
        return int.TryParse(ConfigurationManager.AppSettings[key], out value) && value > 0 ? value : fallback;
    }

    // "60,120,240" → [60, 120, 240]; sai định dạng thì dùng fallback
    static int[] GetIntList(string key, int[] fallback)
    {
        var raw = ConfigurationManager.AppSettings[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        var values = new List<int>();
        foreach (var part in raw.Split(','))
        {
            int v;
            if (!int.TryParse(part.Trim(), out v) || v <= 0) return fallback;
            values.Add(v);
        }
        return values.Count > 0 ? values.ToArray() : fallback;
    }
}

public class NhatotBlockedException : Exception
{
    public NhatotBlockedException(string url) : base($"{url}: nhatot chặn IP (Please try again later!)") { }
}

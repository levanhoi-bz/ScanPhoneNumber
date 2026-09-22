using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Serilog;

/// <summary>
/// Điều tiết request tới nhatot.com dùng chung cho mọi job (HN + HCM).
/// nhatot chặn theo IP: gửi quá dày sẽ bị trả "Please try again later!" cho mọi trang,
/// kể cả khi mở bằng Chrome thường. Vì vậy:
///   - Chỉ 1 request tại một thời điểm, cách nhau ngẫu nhiên MinDelay..MaxDelay giây.
///   - Khi phát hiện bị chặn: tất cả job cùng nghỉ, thời gian nghỉ tăng dần (15 → 30 → 60 phút).
/// </summary>
public static class NhatotRateLimiter
{
    const int MinDelaySeconds = 5;
    const int MaxDelaySeconds = 10;
    static readonly int[] CooldownMinutes = { 15, 30, 60 };

    static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    static readonly Random _random = new Random();
    static readonly object _lock = new object();

    static DateTime _blockedUntil = DateTime.MinValue;
    static DateTime _lastRequestAt = DateTime.MinValue;
    static int _blockCount = 0;

    /// <summary>Chạy <paramref name="action"/> sau khi đã chờ hết thời gian nghỉ / giãn cách.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            await WaitIfBlockedAsync();

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
                lock (_lock) _lastRequestAt = DateTime.Now;
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
}

public class NhatotBlockedException : Exception
{
    public NhatotBlockedException(string url) : base($"{url}: nhatot chặn IP (Please try again later!)") { }
}

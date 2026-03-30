using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

/// <summary>
/// Lấy số điện thoại từ nhatot.com dùng Playwright (headless Chrome).
/// Bypass được Cloudflare 403 vì dùng browser thật.
///
/// Cài đặt:
///   dotnet add package Microsoft.Playwright
///   dotnet build
///   pwsh bin/Debug/net8.0/playwright.ps1 install chromium
/// </summary>
public class NhatotPlaywrightScraper : IAsyncDisposable
{
    private IPlaywright _playwright;
    private IBrowser    _browser;
    private IBrowserContext _context;

    private const string PhoneApiBase = "https://gateway.chotot.com/v1/public/ad-listing/phone";

    // ─── Khởi tạo ────────────────────────────────────────────────────────────

    public async Task InitAsync(bool headless = true)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            // Bỏ comment nếu muốn dùng Chrome cài sẵn thay vì Chromium bundled:
            ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        });

        // Tạo context giống browser thật
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/122.0.0.0 Safari/537.36",
            Locale          = "vi-VN",
            TimezoneId      = "Asia/Ho_Chi_Minh",
            ViewportSize    = new ViewportSize { Width = 1280, Height = 800 },
        });

        // Stealth: ẩn dấu hiệu automation
        await _context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        ");
    }

    // ─── Lấy SĐT từ 1 URL ────────────────────────────────────────────────────

    public async Task<ScrapeResult> GetPhoneAsync(string listingUrl)
    {
        Console.WriteLine($"[*] {listingUrl}");

        if (_context == null)
            throw new InvalidOperationException("Gọi InitAsync() trước.");

        var page = await _context.NewPageAsync();

        try
        {
            // ── Bắt response của phone API (intercept network) ──────────────
            string capturedPhone = null;

            page.Response += async (_, response) =>
            {
                if (response.Url.StartsWith(PhoneApiBase) && response.Status == 200)
                {
                    try
                    {
                        var body = await response.JsonAsync();
                        capturedPhone = body?.GetProperty("phone").GetString();
                    }
                    catch { /* ignore parse errors */ }
                }
            };

            // ── Mở trang listing ────────────────────────────────────────────
            await page.GotoAsync(listingUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30_000,
            });

            // ── Chờ nút "Hiện số" xuất hiện rồi click ─────────────────────
            // Selector có thể thay đổi theo version, thử lần lượt:
            string[] buttonSelectors = new[]
            {
                "button:has-text('Hiện số')",
                "button:has-text('Xem số')",
                "[class*='phone'] button",
                "[data-testid*='phone']",
                "button[class*='phone']",
            };

            ILocator phoneButton = null;
            foreach (var sel in buttonSelectors)
            {
                var loc = page.Locator(sel).First;
                if (await loc.CountAsync() > 0)
                {
                    phoneButton = loc;
                    Console.WriteLine($"    → Tìm thấy nút với selector: {sel}");
                    break;
                }
            }

            if (phoneButton == null)
            {
                // Fallback: thử lấy token từ __NEXT_DATA__ trực tiếp
                Console.WriteLine("    → Không tìm thấy nút, thử lấy token từ __NEXT_DATA__...");
                var phone = await GetPhoneViaTokenAsync(page);
                if (phone != null)
                {
                    Console.WriteLine($"    ✓ {phone}");
                    return ScrapeResult.Ok(listingUrl, phone);
                }
                return ScrapeResult.Fail(listingUrl, "Không tìm thấy nút 'Hiện số'");
            }

            // Click nút và chờ network response
            await phoneButton.ClickAsync();
            await page.WaitForTimeoutAsync(2000); // chờ API response

            if (capturedPhone != null)
            {
                Console.WriteLine($"    ✓ {capturedPhone}");
                return ScrapeResult.Ok(listingUrl, capturedPhone);
            }

            // Fallback: đọc số từ DOM sau khi click
            var phoneText = await TryReadPhoneFromDomAsync(page);
            if (phoneText != null)
            {
                Console.WriteLine($"    ✓ {phoneText} (từ DOM)");
                return ScrapeResult.Ok(listingUrl, phoneText);
            }

            return ScrapeResult.Fail(listingUrl, "Click được nút nhưng không lấy được số");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [!] Lỗi: {ex.Message}");
            return ScrapeResult.Fail(listingUrl, ex.Message);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // ─── Lấy SĐT từ nhiều URL ────────────────────────────────────────────────

    public async Task<List<ScrapeResult>> GetPhonesAsync(
        IEnumerable<string> urls,
        int delayMs = 2000)
    {
        var results = new List<ScrapeResult>();
        var list    = urls.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            var r = await GetPhoneAsync(list[i]);
            results.Add(r);
            Console.WriteLine($"    [{i + 1}/{list.Count}]");

            if (i < list.Count - 1)
                await Task.Delay(delayMs);
        }

        return results;
    }

    // ─── Fallback: lấy token từ __NEXT_DATA__ rồi gọi API ───────────────────

    private async Task<string> GetPhoneViaTokenAsync(IPage page)
    {
        try
        {
            // Đọc __NEXT_DATA__ từ DOM
            var json = await page.EvaluateAsync<string>(
                "() => document.getElementById('__NEXT_DATA__')?.textContent"
            );

            if (string.IsNullOrEmpty(json)) return null;

            var doc = JsonDocument.Parse(json);
            var token = FindKeyRecursive(doc.RootElement, "phone_token");
            if (token == null) return null;

            // Gọi phone API trực tiếp từ trong browser context (tránh bị block)
            var apiUrl = $"{PhoneApiBase}?e={Uri.EscapeDataString(token)}";
            var result = await page.EvaluateAsync<string>($@"
                async () => {{
                    const r = await fetch('{apiUrl}', {{
                        headers: {{ 'Accept': 'application/json' }}
                    }});
                    const d = await r.json();
                    return d.phone ?? null;
                }}
            ");

            return result;
        }
        catch { return null; }
    }

    // ─── Fallback: đọc số từ DOM ─────────────────────────────────────────────

    private static async Task<string> TryReadPhoneFromDomAsync(IPage page)
    {
        // Tìm text khớp định dạng số VN trong trang
        var content = await page.ContentAsync();
        var match   = Regex.Match(content, @"\b(0[35789]\d{8})\b");
        return match.Success ? match.Value : null;
    }

    // ─── Helper: tìm key đệ quy trong JsonElement ────────────────────────────

    private static string FindKeyRecursive(JsonElement el, string key, int depth = 0)
    {
        if (depth > 12) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name == key && p.Value.ValueKind == JsonValueKind.String)
                        return p.Value.GetString();
                    var r = FindKeyRecursive(p.Value, key, depth + 1);
                    if (r != null) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var r = FindKeyRecursive(item, key, depth + 1);
                    if (r != null) return r;
                }
                break;
        }
        return null;
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.DisposeAsync();
        if (_browser  != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

public class ScrapeResult
{
    public string Url { set; get; }
    public string Phone { set; get; }
    public bool Success { set; get; }
    public string Error { set; get; }

    public ScrapeResult(string url, string phone, bool success, string error)
    {
        Url = url;
        Phone = phone;
        Success = success;
        Error = error;
    }
    public static ScrapeResult Ok(string url, string phone)
    {
        return new ScrapeResult(url, phone, true, null);
    }
    public static ScrapeResult Fail(string url, string error)
    {
        return new ScrapeResult(url, null, false, error);
    }

    public string AdId => Url.Split('/').Last().Replace(".htm", "");

    public override string ToString() =>
        Success ? $"{AdId}: {Phone}" : $"{AdId}: LỖI - {Error}";
}

// ─────────────────────────────────────────────────────────────────────────────
// DEMO / ENTRY POINT
// ─────────────────────────────────────────────────────────────────────────────

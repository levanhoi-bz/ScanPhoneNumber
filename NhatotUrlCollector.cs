using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Serilog;

/// <summary>
/// Lấy danh sách URL tin đăng từ các trang tìm kiếm nhatot.com
/// URL pattern: https://www.nhatot.com/mua-ban-bat-dong-san?page={n}
///
/// Cài đặt:
///   dotnet add package Microsoft.Playwright
///   dotnet build
///   pwsh bin/Debug/net8.0/playwright.ps1 install chromium
/// </summary>
public class NhatotUrlCollector
{
    //private IPlaywright     _playwright;
    //private IBrowser        _browser;
    //private IBrowserContext _context;


    // ─── Khởi tạo ────────────────────────────────────────────────────────────

    //public async Task InitAsync(bool headless = true)
    //{
    //    _playwright = await Playwright.CreateAsync();
    //    _browser    = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    //    {
    //        Headless = headless,
    //        ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
    //    });

    //    _context = await _browser.NewContextAsync(new BrowserNewContextOptions
    //    {
    //        UserAgent  = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
    //                     "AppleWebKit/537.36 (KHTML, like Gecko) " +
    //                     "Chrome/122.0.0.0 Safari/537.36",
    //        Locale     = "vi-VN",
    //        TimezoneId = "Asia/Ho_Chi_Minh",
    //        ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
    //    });

    //    await _context.AddInitScriptAsync(
    //        "() => Object.defineProperty(navigator, 'webdriver', { get: () => undefined })"
    //    );
    //}

    //public async Task DestroyAsync()
    //{
    //    try
    //    {
    //        if (_context != null)
    //        {
    //            await _context.CloseAsync();
    //            _context = null;
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        Log.Warning($"DestroyAsync: lỗi đóng context: {ex.Message}");
    //    }

    //    try
    //    {
    //        if (_browser != null)
    //        {
    //            await _browser.CloseAsync();
    //            _browser = null;
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        Log.Warning($"DestroyAsync: lỗi đóng browser: {ex.Message}");
    //    }

    //    try
    //    {
    //        _playwright?.Dispose();
    //        _playwright = null;
    //    }
    //    catch (Exception ex)
    //    {
    //        Log.Warning($"DestroyAsync: lỗi dispose playwright: {ex.Message}");
    //    }
    //}

    // ─── Lấy URL từ 1 trang ──────────────────────────────────────────────────

    public async Task<List<string>> GetUrlsFromPageAsync(string baseUrl, int pageNumber)
    {
        IPlaywright playwright = null;
        IBrowser browser = null;
        IPage page = null;
        IBrowserContext context = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            browser    = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                Args     = new[]
                {
                "--no-sandbox",
                "--disable-dev-shm-usage",   // tránh /dev/shm OOM
                "--disable-gpu",
                "--disable-extensions",
                "--bwsi",                    // no sign-in
                "--disable-background-networking",
            }
            });

            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                            "AppleWebKit/537.36 (KHTML, like Gecko) " +
                            "Chrome/124.0.0.0 Safari/537.36",
            });


            var searchUrl = $"{baseUrl}?page={pageNumber}";
            Console.WriteLine($"[Page {pageNumber}] {searchUrl}");

            page = await context.NewPageAsync();

            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30_000,
            });

            // Chờ danh sách tin đăng load xong
            // Thử các selector thường gặp của nhatot
            await WaitForListingAsync(page);

            // Lấy tất cả href trỏ đến trang tin đăng
            var urls = await ExtractListingUrlsAsync(page);

            Console.WriteLine($"  → Tìm thấy {urls.Count} URL");
            return urls;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"  [!] Timeout trang {pageNumber}");
            return new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [!] Lỗi trang {pageNumber}: {ex.Message}");
            return new List<string>();
        }
        finally
        {
            // ── Luôn cleanup dù exception ───────────────────────────────────

            await page.CloseAsync();
            if (context != null) await context.DisposeAsync();
            if (browser  != null) await browser.DisposeAsync();
            playwright?.Dispose();
        }
    }

    // ─── Lấy URL từ nhiều trang ───────────────────────────────────────────────

    /// <summary>
    /// Lấy URL từ trang 1 đến trang <paramref name="maxPage"/>.
    /// Tự dừng sớm nếu trang trả về rỗng (hết tin).
    /// </summary>
    public async Task<List<string>> GetAllUrlsAsync(
        string baseUrl,
        int currentPage,
        bool stopEmpty = true)
    {
        var all = new List<string>();

        //await InitAsync(true);

        var urls = await GetUrlsFromPageAsync(baseUrl,currentPage);

        if (urls.Count == 0 && stopEmpty)
        {
            Log.Information($"  → Trang {currentPage} rỗng, dừng lại.");
            return all;
        }

        // Loại bỏ trùng lặp
        var newUrls = urls.Except(all).ToList();
        all.AddRange(newUrls);

        //await DestroyAsync();

        return all;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task WaitForListingAsync(IPage page)
    {
        // Thử từng selector cho đến khi tìm được hoặc timeout
        string[] listingSelectors = new[]
        {
            "a[href*='.htm']",           // link đến tin đăng
            "[class*='AdItem']",         // component tin đăng
            "[class*='listing']",
            "[class*='ad-item']",
            "article",
        };

        foreach (var sel in listingSelectors)
        {
            try
            {
                await page.WaitForSelectorAsync(sel, new PageWaitForSelectorOptions
                {
                    Timeout = 8000,
                    State   = WaitForSelectorState.Attached,
                });
                return; // tìm thấy rồi
            }
            catch (TimeoutException ex) {
                Log.Information(ex.ToString());
            }
        }
    }

    private static async Task<List<string>> ExtractListingUrlsAsync(IPage page)
    {
        // Lấy tất cả href trong trang
        var allHrefs = await page.EvaluateAsync<string[]>(@"
            () => Array.from(document.querySelectorAll('a[href]'))
                       .map(a => a.href)
        ");

        if (allHrefs == null) return new List<string>();

        // Lọc chỉ lấy URL tin đăng (kết thúc bằng số ID + .htm)
        return allHrefs
            .Where(IsListingUrl)
            .Select(NormalizeUrl)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    // URL tin đăng có dạng: /mua-ban-.../[id].htm
    // ví dụ: https://www.nhatot.com/mua-ban-nha-dat-quan-1/131234567.htm
    private static bool IsListingUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.Contains("nhatot.com"))    return false;
        if (!url.EndsWith(".htm"))          return false;

        // Phần cuối trước .htm phải là số (ad ID)
        var path    = new Uri(url).AbsolutePath;
        var segment = path.Split('/').Last().Replace(".htm", "");
        return segment.Length > 6 && segment.All(char.IsDigit);
    }

    private static string NormalizeUrl(string url)
    {
        // Bỏ query string nếu có, giữ nguyên path
        var uri = new Uri(url);
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }

    //public async ValueTask DisposeAsync()
    //{
    //    if (_context != null) await _context.DisposeAsync();
    //    if (_browser  != null) await _browser.DisposeAsync();
    //    _playwright?.Dispose();
    //}
}

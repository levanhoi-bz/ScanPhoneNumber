using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using System.Net;
using System.Security.Policy;
using System.IO;
using System.Net.Sockets;
using Serilog;
using System.Buffers.Text;
using System.Linq;
using Leaf.xNet;
using System.Text;
using System.Windows.Forms;

namespace ScanPhoneNumber
{
   public class Scan
    {
        static string dbPath = "phones2.db";
        static string botToken = "8149070648:AAHfPGuO8J-DGf8BvTYNZYpzwbXe8Ywv3k4";  // Thay bằng token bot của bạn
        static string chatId = "7270993010";      // Thay bằng chat ID
        static string TablePhoneNumber = "PhoneNumbers";
        static bool IsSendTelegram = true;

        const string baseUrl_HN_BanNhaDat = "https://alonhadat.com.vn/nha-moi-gioi/ban-nha-dat/ha-noi-t1.html";
        const int Max_HN_BanNhaDat = 2300;

        const string baseUrl_HCM_BanNhaDat = "https://alonhadat.com.vn/nha-moi-gioi/ban-nha-dat/ho-chi-minh-t2.html";
        const int Max_HCM_BanNhaDat = 2000;

        const string baseUrl_HN_ChoThue = "https://alonhadat.com.vn/nha-moi-gioi/cho-thue-nha-dat/ha-noi-t1.html";
        const int Max_HN_ChoThue = 540;

        const string baseUrl_HCM_ChoThue = "https://alonhadat.com.vn/nha-moi-gioi/cho-thue-nha-dat/ho-chi-minh-t2.html";
        const int Max_HCM_ChoThue = 262;

        public static async Task Init()
        {
            // Khởi tạo DB nếu chưa có
            InitializeDatabase();

            Log.Information("Start run job...");

            RunJobAsync(baseUrl_HN_BanNhaDat, Max_HN_BanNhaDat);

            RunJobAsync(baseUrl_HCM_BanNhaDat, Max_HCM_BanNhaDat);

            RunJobAsync(baseUrl_HN_ChoThue, Max_HN_ChoThue);

            RunJobAsync(baseUrl_HCM_ChoThue, Max_HCM_ChoThue);

            Log.Information("Nhan Enter de thoat...");
            Console.ReadLine();
        }
        static async Task RunJobAsync(string baseUrl, int maxPerPage)
        {
            int pageCurrent = 0;
            while (pageCurrent < maxPerPage)
            {
                int pageStart = pageCurrent + 1;
                int pageEnd = pageCurrent + 600;
                if (pageEnd > maxPerPage) pageEnd = maxPerPage;

                Task.Run(() => RunJobAsync(baseUrl, pageStart, pageEnd));

                pageCurrent = pageEnd;
            }
        }

        static async Task RunJobAsync(string baseUrl, int startPage, int endPage)
        {
            while (true)
            {
                Log.Information($"Job chay luc: {DateTime.Now} cho url = {baseUrl}, startPage = {startPage}, endPage = {endPage} ");

                try
                {
                    await ScrapeAndSavePhones(baseUrl, startPage, endPage);
                }
                catch (Exception ex)
                {
                    Log.Error($"Loi: {ex.Message}");
                }

                Random random = new Random();
                await Task.Delay(TimeSpan.FromSeconds(1)); // Chạy mỗi 5 giây
            }
        }

        public static void InitializeDatabase()
        {
            var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            conn.Open();
            var cmd = new SQLiteCommand(@"
            CREATE TABLE IF NOT EXISTS "+TablePhoneNumber +@" (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PhoneNumber TEXT UNIQUE NOT NULL,
                    Url TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(""PhoneNumber"")
                );
            CREATE INDEX IF NOT EXISTS idx_phone_number ON PhoneNumbers(PhoneNumber);

             CREATE TABLE IF NOT EXISTS PhoneNumbersTelegram (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PhoneNumber TEXT UNIQUE NOT NULL,
                    Url TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );

            CREATE TABLE IF NOT EXISTS PageTracking (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Url TEXT NOT NULL,
                PageNumber INTEGER NOT NULL
            );", conn);
            cmd.ExecuteNonQuery();
        }

        public static string GetPhoneNumbersCreatedAfterOneWeek()
        {
            StringBuilder result = new StringBuilder();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
                {
                    conn.Open();

                    string sql = @"
                    SELECT PhoneNumber, CreatedAt 
                    FROM PhoneNumbersTelegram
                    WHERE CreatedAt > datetime('now', '-700 days')
                    ORDER BY CreatedAt DESC;
                ";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string phone = reader["PhoneNumber"].ToString();
                            DateTime createdAt = DateTime.Parse(reader["CreatedAt"].ToString());

                            // format: 09887232323 08:00:12 12/01/2025
                            string line = $"{phone} {createdAt.ToLocalTime():HH:mm:ss dd/MM/yyyy}";
                            result.AppendLine(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return "Lỗi: " + ex.Message;
            }

            return result.ToString();
        }

        public static async Task ScrapeAndSavePhones(string baseUrl, int startPage, int endPage)
        {
            try
            {
                int page = startPage;// GetLastPageNumber(baseUrl);
                Log.Information($"Start Scan URL: {baseUrl}");

                while (page < endPage)
                {
                    Log.Information($"Job chay trang: {page}");

                    List<string> newPhones = new List<string>();

                    string url = $"{baseUrl}?p={page}";
                    List<string> phones = ScrapePhones_Proxy(url, out bool isOK);
                    if (!isOK)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        continue;
                    }

                    foreach (var phone in phones)
                        if (SaveToDatabasePhoneNumber(phone, url))
                            newPhones.Add(phone);

                    if (phones.Count > 0)
                        SavePageNumber(page, baseUrl);// Lưu trang đã quét

                    if (IsSendTelegram)
                        // Nếu có số mới, gửi vào Telegram
                        if (newPhones.Count > 0 && newPhones.Count < phones.Count)
                        {
                            string message = $"{url}\n" + string.Join("\n", newPhones);
                            await SendTelegramMessage(message);

                            foreach (var phoneNumber in newPhones)
                                SaveToDatabaseTelegram(phoneNumber, url);
                        }

                    page++;

                    await Task.Delay(TimeSpan.FromSeconds(1)); // Chạy mỗi 1 giây
                }
            }
            catch (Exception ex)
            {
                Log.Error($"❌ Lỗi: {ex.Message}");
            }
        }

        public static List<string> ScrapePhones_Proxy(string url, out bool isOK)
        {
            isOK = true;
            List<string> phoneNumbers = new List<string>();

            ChangeTorIP(); // Đổi IP trước mỗi request

            Log.Information($"Lay noi dung trang: {url}");
            DateTime start = DateTime.Now;
            string html = GetContentHTML_Proxy2(url);

            Log.Information($"Tong Thoi gian lay: {(DateTime.Now-start).TotalSeconds}");

            if (html == "" || html == null)
            {
                isOK = false;
                return phoneNumbers;
            }

            if (html.Contains("Vui lòng xác minh không phải Robot"))
            {
                Log.Information($"La Robot: {url}");
                isOK = false;
                return phoneNumbers;
            }

            try
            {
                HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var phoneNodes = doc.DocumentNode.SelectNodes("//a[starts-with(@href, 'tel:')]");

                if (phoneNodes != null)
                {
                    phoneNumbers = phoneNodes
                        .Select(node => node.Attributes["href"].Value.Replace("tel:", "").Trim().Replace(".", ""))
                        .Distinct()
                        .ToList();
                }

            }
            catch (Exception ex)
            {
                Log.Error($"Loi khi lay du lieu: {ex.Message}");
                isOK = false;
            }

            Log.Information($"Lay duoc sdt: {string.Join(",", phoneNumbers)}");
            return phoneNumbers;
        }

        static void ChangeTorIP()
        {
            while (true)
            {
                try
                {
                    using (var client = new TcpClient("127.0.0.1", 9051))
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream))
                    using (var reader = new StreamReader(stream))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine("AUTHENTICATE \"abcd@1234\"");
                        writer.WriteLine("SIGNAL NEWNYM"); // Yêu cầu IP mới
                        writer.WriteLine("QUIT");

                        string response = reader.ReadToEnd();
                        Log.Information("Doi IP thanh cong: " + response.Replace("\r\n"," "));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Loi khi doi IP: " + ex.Message);
                    Task.Delay(TimeSpan.FromSeconds(5)); // Chạy mỗi 30 giây
                }
            }
        }

        public static string GetContentHTML_Proxy2(string url)
        {
            string proxyAddress = "127.0.0.1";
            int proxyPort = 9050;

            try
            {
                using (var request = new HttpRequest())
                {
                    // Cấu hình Proxy SOCKS5
                    request.Proxy = new Socks5ProxyClient(proxyAddress, proxyPort);
                    request.Proxy.ConnectTimeout = 10000; // Timeout 10s

                    // Giả lập trình duyệt (User-Agent)
                    request.UserAgent = Http.ChromeUserAgent();
                    request.AddHeader("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
                    request.AddHeader("Accept-Language", "en-US,en;q=0.9");
                    request.AddHeader("Referer", "https://www.google.com/");
                    request.AddHeader("DNT", "1");

                    // Gửi request GET và lấy HTML
                    string content = request.Get(url).ToString();

                    return content;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Lỗi khi kết nối Tor: " + ex.ToString());
                return "";
            }
        }

        public static string GetContentHTML_Proxy(string url)
        {
            string proxyAddress = "127.0.0.1";
            int proxyPort = 9050;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = new WebProxy($"socks5://{proxyAddress}:{proxyPort}");
                // Giả lập trình duyệt
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Referer", "https://www.google.com/");
                request.Headers.Add("DNT", "1");
                //request.Headers.Add("Accept-Encoding", "gzip, deflate");
                request.KeepAlive = true;
                request.CookieContainer = new CookieContainer();
                request.Timeout = 2 * 60 * 1000;
                request.ReadWriteTimeout = 2 * 60 * 1000;

                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    string content = reader.ReadToEnd();
                    //Log.Information("Tor đang hoạt động! Kiểm tra IP:");
                    //Log.Information(content);
                    return content;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Lỗi khi kết nối Tor: " + ex.ToString());
                return "";
            }
        }
        
        public static bool SaveToDatabasePhoneNumber(string phoneNumber, string url)
        {
            var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            conn.Open();

            // Kiểm tra số đã tồn tại chưa
            var checkCmd = new SQLiteCommand($"SELECT COUNT(*) FROM {TablePhoneNumber} WHERE PhoneNumber = @phone", conn);
            checkCmd.Parameters.AddWithValue("@phone", phoneNumber);
            long count = (long)checkCmd.ExecuteScalar();

            if (count == 0)
            {
                // Chưa có -> thêm mới
                var insertCmd = new SQLiteCommand($"INSERT INTO {TablePhoneNumber} (PhoneNumber, Url) VALUES (@PhoneNumber, @Url)", conn);
                insertCmd.Parameters.AddWithValue("@PhoneNumber", phoneNumber);
                insertCmd.Parameters.AddWithValue("@Url", url);
                insertCmd.ExecuteNonQuery();
                Log.Information($"Da luu so moi: {phoneNumber}");
                return true;
            }
            return false;
        }
        static void SaveToDatabaseTelegram(string phoneNumber, string url)
        {
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string insertQuery = "INSERT INTO PhoneNumbersTelegram (PhoneNumber, Url) VALUES (@PhoneNumber, @Url)";
                using (var command = new SQLiteCommand(insertQuery, connection))
                {
                    command.Parameters.AddWithValue("@PhoneNumber", phoneNumber);
                    command.Parameters.AddWithValue("@Url", url);
                    command.ExecuteNonQuery();
                }
            }

            Log.Information("[INSERT PhoneNumbersTelegram]" + phoneNumber + " " + DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy"));
        }
        static int GetLastPageNumber(string url)
        {
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string query = "SELECT MAX(PageNumber) FROM PageTracking WHERE Url = @Url";
                using (var command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Url", url);
                    var result = command.ExecuteScalar();
                    return result != DBNull.Value ? Convert.ToInt32(result) : 1;
                }
            }
        }
        static void SavePageNumber(int pageNumber, string url)
        {
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string insertQuery = "INSERT INTO PageTracking (PageNumber, Url) VALUES (@PageNumber, @Url)";
                using (var command = new SQLiteCommand(insertQuery, connection))
                {
                    command.Parameters.AddWithValue("@PageNumber", pageNumber);
                    command.Parameters.AddWithValue("@Url", url);
                    command.ExecuteNonQuery();
                }
            }
        }
        public static async Task SendTelegramMessage(string message)
        {
            HttpClient client = new HttpClient();
            string url = $"https://api.telegram.org/bot{botToken}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

            HttpResponseMessage response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Da gui thong bao Telegram: " + message);
            }
            else
            {
                Log.Information("❌ Gửi Telegram thất bại!");
            }
        }



        static async Task<List<string>> GetProxies()
        {
            string proxyApiUrl = "https://proxylist.geonode.com/api/proxy-list?limit=10&page=1&sort_by=lastChecked&sort_type=desc";

            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10); // Set timeout 10s

            try
            {
                string json = GetProxyDataWithHttpWebRequest();
                Log.Information("✅ API Proxy Response: " + json);

                dynamic data = JsonConvert.DeserializeObject(json);
                List<string> proxies = new List<string>();

                foreach (var proxy in data.data)
                {
                    string ip = proxy.ip;
                    string port = proxy.port;
                    proxies.Add($"{ip}:{port}");
                }

                return proxies;
            }
            catch (TaskCanceledException)
            {
                Log.Information("❌ Timeout khi gọi API Proxy");
            }
            catch (HttpRequestException ex)
            {
                Log.Information($"❌ Lỗi HTTP: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Information($"❌ Lỗi khác: {ex.Message}");
            }

            return new List<string>();
        }
        static string GetProxyDataWithHttpWebRequest()
        {
            string url = "https://proxylist.geonode.com/api/proxy-list?limit=10&page=1&sort_by=lastChecked&sort_type=desc";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);

            request.Method = "GET";
            request.UserAgent = "Mozilla/5.0";
            request.Timeout = 10000; // 10 giây

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                Log.Information($"❌ Lỗi HttpWebRequest: {ex.Message}");
                return null;
            }
        }

        //static async Task ScrapePhonesWithRotatingProxy()
        //{
        //    List<string> proxies = await GetProxies();
        //    int proxyIndex = 0;

        //    foreach (var proxy in proxies)
        //    {
        //        Log.Information($"🔄 Đang dùng proxy: {proxy}");
        //        try
        //        {
        //            ChromeOptions options = new ChromeOptions();
        //            options.AddArgument($"--proxy-server=http://{proxy}");
        //            options.AddArgument("--disable-blink-features=AutomationControlled");
        //            options.AddArgument("--headless"); // Chạy ẩn nếu không cần giao diện
        //            options.AddArgument("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.6167.85 Safari/537.36");

        //            using IWebDriver driver = new ChromeDriver(options);
        //            driver.Navigate().GoToUrl("https://alonhadat.com.vn/nha-moi-gioi/ban-nha-dat.html");

        //            Task.Delay(5000).Wait(); // Chờ trang tải

        //            var phoneElements = driver.FindElements(By.XPath("//a[contains(@href, 'tel:')]"));
        //            var phones = phoneElements.Select(e => e.Text).ToList();

        //            Log.Information($"📞 Tìm thấy {phones.Count} số điện thoại:");
        //            phones.ForEach(Log.Information);
        //            if (phones.Count == 0)
        //                Log.Information($"Title: {driver.Title}, PageSource: {driver.PageSource}");

        //            driver.Quit();
        //            break; // Nếu thành công thì dừng, không đổi proxy nữa
        //        }
        //        catch (Exception ex)
        //        {
        //            Log.Information($"❌ Lỗi với proxy {proxy}: {ex.Message}");
        //            proxyIndex++;
        //            if (proxyIndex >= proxies.Count)
        //            {
        //                Log.Information("❌ Hết proxy khả dụng!");
        //                break;
        //            }
        //        }
        //    }
        //}
        //public static async Task<List<string>> ScrapePhones(string url)
        //{
        //    var proxy = new WebProxy("socks5://127.0.0.1:9050");
        //    var httpClientHandler = new HttpClientHandler { Proxy = proxy };
        //    var httpClient = new HttpClient(httpClientHandler);

        //    var response = await httpClient.GetStringAsync(url);

        //    ChromeOptions options = new ChromeOptions();
        //    options.AddArgument("--headless"); // Chạy ẩn (bỏ dòng này nếu muốn kiểm tra giao diện)
        //    options.AddArgument("--disable-blink-features=AutomationControlled"); // Tránh bị phát hiện là bot 
        //    options.AddArgument("--disable-gpu");
        //    options.AddArgument("--no-sandbox");

        //    IWebDriver driver = new ChromeDriver(options);
        //    driver.Manage().Window.Size = new System.Drawing.Size(1920, 1080); // Đảm bảo kích thước lớn
        //    driver.Navigate().GoToUrl(url);

        //    Random random = new Random();
        //    Thread.Sleep(random.Next(5000, 8000)); // Chờ ngẫu nhiên 2-5 giây

        //    // Giả lập di chuột
        //    Actions actions = new Actions(driver);
        //    int countMove = 0;
        //    for (int i = 0; i < 50; i++)
        //    {
        //        var bodyElement = driver.FindElement(By.TagName("body"));
        //        var rect = bodyElement.Size;

        //        int offsetX = random.Next(100, 500);
        //        int offsetY = random.Next(100, 500);

        //        try
        //        {
        //            actions.MoveToElement(bodyElement, offsetX, offsetY).Perform();
        //            countMove++;
        //            if (countMove>=5) break;
        //            if (countMove>=3) if (i>5) break;
        //            if (countMove>=2) if (i>10) break;
        //        }
        //        catch (Exception ex)
        //        {
        //            Log.Error($"❌ Lỗi: {ex.Message}, xOffset: {offsetX}, yOffset: {offsetY}");
        //            Thread.Sleep(50);
        //        }
        //        Thread.Sleep(random.Next(1000, 3000));



        //        //// Lấy kích thước trình duyệt hiện tại
        //        //int browserWidth = driver.Manage().Window.Size.Width;
        //        //int browserHeight = driver.Manage().Window.Size.Height;

        //        //if (browserWidth > 20 && browserHeight>20)
        //        //{
        //        //    int xOffset = random.Next(20, Math.Min(browserWidth - 20, 500));
        //        //    int yOffset = random.Next(20, Math.Min(browserHeight - 20, 500));
        //        //    try
        //        //    {
        //        //        actions.MoveByOffset(xOffset, yOffset).Perform();
        //        //        countMove++;
        //        //        if (countMove>=5) break;
        //        //        if (countMove>=3) if (i>5) break;
        //        //        if (countMove>=2) if (i>10) break;
        //        //    }
        //        //    catch (Exception ex)
        //        //    {
        //        //        Log.Information($"❌ Lỗi: {ex.Message}, xOffset: {xOffset}, yOffset: {yOffset}");
        //        //        Thread.Sleep(5);
        //        //    }
        //        //    Thread.Sleep(random.Next(100, 3000));

        //        //}
        //    }

        //    // Lấy danh sách số điện thoại
        //    var phoneElements = driver.FindElements(By.XPath("//a[contains(@href, 'tel:')]"));

        //    var phones = phoneElements.Select(e => e.Text.Replace(".", "")).ToList();

        //    Log.Information($"📞 Tìm thấy {phones.Count} số điện thoại:");
        //    if (phones.Count == 0)
        //        Log.Information($"Title: {driver.Title}, PageSource: {driver.PageSource}");

        //    phones.ForEach(Log.Information);

        //    driver.Quit(); // Đóng trình duyệt

        //    return phones;
        //}
    }
}

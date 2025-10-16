using Leaf.xNet;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace BDS
{
    internal class HTMLHelper
    {
       public static void ChangeTorIP()
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
                        //Log.Information("Doi IP thanh cong: " + response.Replace("\r\n"," "));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Loi khi Doi IP: " + ex.Message);
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

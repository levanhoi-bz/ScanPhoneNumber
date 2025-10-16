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
using BDS;

namespace ScanPhoneNumber
{
   public class Scanner
    {        
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
            DBM.InitializeDatabase();

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

        
        public static async Task ScrapeAndSavePhones(string baseUrl, int startPage, int endPage)
        {
            try
            {
                int page = startPage;// GetLastPageNumber(baseUrl);
                Log.Information($"Start Scan URL: {baseUrl}");

                while (page < endPage)
                {
                    //Log.Information($"Job chay trang: {page}");

                    List<string> newPhones = new List<string>();

                    string url = $"{baseUrl}?p={page}";
                    var phones = ScrapePhones_Proxy(url, out bool isOK);
                    if (!isOK)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        continue;
                    }

                    foreach (var item in phones)
                        if (DBM.SaveToDatabasePhoneNumber(item.Key, item.Value, url))
                            newPhones.Add(item.Value);

                    if (phones.Count > 0)
                        DBM.SavePageNumber(page, baseUrl);// Lưu trang đã quét

                    if (IsSendTelegram)
                        // Nếu có số mới, gửi vào Telegram
                        if (newPhones.Count > 0) // && newPhones.Count < phones.Count)
                        {
                            string message = $"{url}\n" + string.Join("\n", newPhones);
                            await TelegramHelper.SendTelegramMessage(message);

                            foreach (var phoneNumber in newPhones)
                                DBM.SaveToDatabaseTelegram(phoneNumber, url);
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

        public static Dictionary<long, string> ScrapePhones_Proxy(string url, out bool isOK)
        {
            isOK = true;
            Dictionary<long,string> phoneNumbers = new Dictionary<long, string>();

            HTMLHelper.ChangeTorIP(); // Đổi IP trước mỗi request

            Log.Information($"Lay noi dung trang: {url}");
            DateTime start = DateTime.Now;
            string html = HTMLHelper.GetContentHTML_Proxy2(url);

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

            List<string> linkProfiles = GetProfileLinks(html);
            foreach (var linkProfile in linkProfiles)
            {
                string phoneNumber = "";

                string strProfileID = ExtractProfileId_Simple(linkProfile);
                if (!long.TryParse(strProfileID, out long profileId))
                {
                    Log.Information($"ProfileID khong hợp lệ: {linkProfile}");
                    continue;
                }

                phoneNumber = DBM.GetPhoneNumberByProfileId(profileId);
                if (!string.IsNullOrEmpty(phoneNumber))
                    continue;

                HTMLHelper.ChangeTorIP();

                Log.Information($"Lay noi dung trang: {linkProfile}");
                Task.Delay(TimeSpan.FromSeconds(1));
                string htmlProfile = HTMLHelper.GetContentHTML_Proxy2(linkProfile);
                //Log.Information($"Tong Thoi gian lay: {(DateTime.Now-start).TotalSeconds}");

                if (htmlProfile == "" || htmlProfile == null)
                    continue;

                if (htmlProfile.Contains("Vui lòng xác minh không phải Robot"))
                {
                    Log.Information($"La Robot: {linkProfile}");
                    continue;
                }

                phoneNumber = GetPhoneNumber(htmlProfile);
                if (!string.IsNullOrEmpty(phoneNumber))
                    phoneNumbers[profileId] = phoneNumber;               
            }

            Log.Information($"Lay duoc sdt: {string.Join(",", phoneNumbers)}");

            return phoneNumbers;
        }     
        public static string GetPhoneNumber(string htmlProfile)
        {
            try
            {
                HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(htmlProfile);

                // Tìm phần div có class = "agent-infor"
                var agentDiv = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'agent-infor')]");

                if (agentDiv != null)
                {
                    // Trong div đó, tìm thẻ <a> có href bắt đầu bằng "tel:"
                    var phoneLink = agentDiv.SelectSingleNode(".//a[starts-with(@href, 'tel:')]");
                    if (phoneLink != null)
                    {
                        // Lấy nội dung text (số điện thoại hiển thị)
                        string phoneText = phoneLink.InnerText.Trim();

                        // Hoặc lấy từ href
                        string phoneHref = phoneLink.GetAttributeValue("href", "").Replace("tel:", "").Trim();

                        // Trả về định dạng thống nhất (ví dụ: "0969392318")
                        return phoneHref;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Lỗi khi đọc số điện thoại: " + ex.Message);
            }

            return "";
        }

        public static List<string> GetProfileLinks(string html)
        {
            var links = new List<string>();

            try
            {
                HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                // Tìm tất cả các thẻ <a> có text chứa "Xem trang cá nhân"
                var nodes = doc.DocumentNode.SelectNodes("//a[contains(text(), 'Xem trang cá nhân')]");

                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        string href = node.GetAttributeValue("href", "").Trim();
                        if (!string.IsNullOrEmpty(href))
                        {
                            // Chuyển link tương đối thành link tuyệt đối
                            if (href.StartsWith("/"))
                                href = "https://alonhadat.com.vn" + href;

                            links.Add(href);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Lỗi: " + ex.Message);
            }

            return links;
        }
        public static string ExtractProfileId_Simple(string url)
        {
            try
            {
                // Lấy phần cuối sau dấu "/"
                string lastPart = url.Split('/').Last();

                // Ví dụ: "ngu-861861.html" → tách giữa dấu '-' và '.'
                string idPart = lastPart.Split('-').Last().Replace(".html", "");
                return idPart;
            }
            catch
            {
                return "";
            }
        }      
    }
}

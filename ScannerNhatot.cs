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
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace ScanPhoneNumber
{
   public class ScannerNhatot
    {        
        static bool IsSendTelegram = false;

        const string baseUrl_HN = "https://www.nhatot.com/mua-ban-bat-dong-san-ha-noi";
        const int Max_HN = 400;

        const string baseUrl_HCM = "https://www.nhatot.com/mua-ban-bat-dong-san-tp-ho-chi-minh";
        const int Max_HCM = 1000;

        public static async Task Init()
        {
            // Khởi tạo DB nếu chưa có
            DBM.InitializeDatabase();

            Log.Information("Start run job...");

            RunJobAsync(baseUrl_HN, Max_HN);
            RunJobAsync(baseUrl_HCM, Max_HCM);

            Log.Information("Nhan Enter de thoat...");
            Console.ReadLine();
        }
        static async Task RunJobAsync(string baseUrl, int maxPerPage)
        {
            int pageCurrent = 0;
            while (pageCurrent < maxPerPage)
            {
                int pageStart = pageCurrent + 1;
                int pageEnd = pageCurrent + 10000;
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
                await Task.Delay(TimeSpan.FromSeconds(2)); // Chạy mỗi 5 giây
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
                    Log.Information($"Job chay trang: {baseUrl}?page={page}");

                    List<string> newPhones = new List<string>();

                    var collector = new NhatotUrlCollector();
                    var urls = await collector.GetAllUrlsAsync(baseUrl, page);

                    if(urls.Count == 0)
                    {
                        Log.Information($"Trang {page} rỗng, dừng lại.");
                        break;
                    }

                    var scraper = new NhatotPlaywrightScraper();
                    await scraper.InitAsync(headless: true);

                    var phones = await scraper.GetPhonesAsync(urls);
                    if(phones.Count == 0)
                    {
                        Log.Information($"Trang {page} không tìm thấy số điện thoại, dừng lại.");
                        continue;
                    }
                    if (phones.Count(v=>!v.Success) > 0)
                        Log.Information($"{baseUrl}?page={page}: {string.Join("\n", phones.Where(v => !v.Success).Select(u => u.Error))}.");

                    if (phones.Count(v => v.Success) > 0)
                        Log.Information($"{baseUrl}?page={page}: {string.Join("\n", phones.Where(v => v.Success).Select(u => u.Phone))}.");

                    foreach (var item in phones)
                        if (item.Success)
                        {
                            if (DBM.SaveToDatabasePhoneNumber(0, item.Phone, item.Url))
                                newPhones.Add(item.Phone);
                        }

                    if (phones.Count > 0)
                        DBM.SavePageNumber(page, baseUrl);// Lưu trang đã quét

                    if (IsSendTelegram)
                        // Nếu có số mới, gửi vào Telegram
                        if (newPhones.Count > 0) // && newPhones.Count < phones.Count)
                        {
                            string message = $"{baseUrl}?page={page}\n" + string.Join("\n", newPhones);
                            await TelegramHelper.SendTelegramMessage(message);

                            foreach (var phoneNumber in newPhones)
                                DBM.SaveToDatabaseTelegram(phoneNumber, $"{baseUrl}?page={page}");
                        }

                    page++;

                    await Task.Delay(TimeSpan.FromSeconds(2)); // Chạy mỗi 1 giây
                }
            }
            catch (Exception ex)
            {
                Log.Error($"❌ Lỗi: {ex.Message}");
            }
        }      
    }
}

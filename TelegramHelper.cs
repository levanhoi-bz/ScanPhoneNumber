using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace BDS
{
    public class TelegramHelper
    {
        // Telegram gioi han 4096 ky tu/message. Chua dem 200 de con cho phan tieu de.
        private const int MaxMessageLength = 3800;

        // HttpClient tinh dung lai connection. Tao moi moi lan gui se can kiet socket.
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        static TelegramHelper()
        {
            // .NET Framework co the mac dinh ve SSL3/TLS1.0 tuy phien ban Windows va cau hinh may.
            // api.telegram.org chi nhan TLS 1.2 tro len -> khong bat tay duoc, loi bao ra chi la
            // "Could not create SSL/TLS secure channel". Bat TLS 1.2 tuong minh de khong phu thuoc
            // mac dinh cua may chu.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private static string BotToken => ConfigurationManager.AppSettings["TelegramBotToken"] ?? "";
        private static string ChatId => ConfigurationManager.AppSettings["TelegramChatId"] ?? "";

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);

        /// <summary>
        /// Gui mot message. Tra ve true CHI KHI Telegram xac nhan ok.
        /// Moi truong hop that bai deu log kem status code va body - day la thu
        /// thieu o ban cu, khien bot chet 1 tuan ma khong ai biet.
        /// </summary>
        public static async Task<bool> SendTelegramMessage(string message)
        {
            if (!IsConfigured)
            {
                Log.Error("[TELEGRAM] Chua cau hinh TelegramBotToken / TelegramChatId trong App.config.");
                return false;
            }

            if (message.Length > MaxMessageLength)
            {
                Log.Warning($"[TELEGRAM] Message dai {message.Length} ky tu, cat con {MaxMessageLength}.");
                message = message.Substring(0, MaxMessageLength);
            }

            try
            {
                string url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
                var payload = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("chat_id", ChatId),
                    new KeyValuePair<string, string>("text", message),
                    new KeyValuePair<string, string>("disable_web_page_preview", "true")
                });

                using (var response = await Client.PostAsync(url, payload))
                {
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                        return true;

                    Log.Error($"[TELEGRAM] Gui that bai HTTP {(int)response.StatusCode} {response.StatusCode}. Telegram tra ve: {body}");

                    if ((int)response.StatusCode == 401)
                        Log.Error("[TELEGRAM] 401 Unauthorized = token da bi thu hoi hoac bot da bi xoa/ban. Kiem tra BotFather.");

                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[TELEGRAM] Loi khi gui: {Describe(ex)}");
                return false;
            }
        }

        /// <summary>
        /// HttpRequestException.Message chi noi "An error occurred while sending the request" -
        /// nguyen nhan that (DNS, TLS, proxy, firewall) nam trong InnerException. Duyet het chuoi.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append($"[{e.GetType().Name}] {e.Message}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Goi getMe luc khoi dong de biet ngay token con song hay khong,
        /// thay vi chay mu roi phat hien sau mot tuan.
        /// </summary>
        public static async Task<bool> HealthCheck()
        {
            if (!IsConfigured)
            {
                Log.Error("[TELEGRAM] Health check bo qua: chua cau hinh token/chat id.");
                return false;
            }

            try
            {
                using (var response = await Client.GetAsync($"https://api.telegram.org/bot{BotToken}/getMe"))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        Log.Information($"[TELEGRAM] Health check OK: {body}");
                        return true;
                    }

                    Log.Error($"[TELEGRAM] Health check THAT BAI HTTP {(int)response.StatusCode}: {body}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[TELEGRAM] Health check loi: {Describe(ex)}");
                return false;
            }
        }
    }
}

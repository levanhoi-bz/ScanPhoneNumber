using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BDS
{
    public class TelegramHelper
    {
        static string botToken = "8149070648:AAHfPGuO8J-DGf8BvTYNZYpzwbXe8Ywv3k4";  // Thay bằng token bot của bạn
        static string chatId = "7270993010";      // Thay bằng chat ID
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
    }
}

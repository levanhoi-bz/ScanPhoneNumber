using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BDS
{
    /// <summary>
    /// Hang doi gui Telegram theo kieu outbox: scanner chi ghi so vao DB,
    /// dispatcher nay doc cac so co TelegramSentAt IS NULL roi gui theo lo.
    ///
    /// App tat giua chung khong mat so nao - lan chay sau hang doi van con nguyen.
    /// </summary>
    public static class TelegramDispatcher
    {
        // So dien thoai moi message.
        private static int ChunkSize => ReadInt("TelegramChunkSize", 50);

        // Tran cung moi chu ky. Day la thu chan burst: khong co no, mot dot crawl
        // 6.000 so se ban 120 message lien tiep tu mot bot vua tao - dung chan dung spam bot.
        // Chay deu chi ~20-90 so/ngay nen khong bao gio cham tran nay.
        private static int MaxPerCycle => ReadInt("TelegramMaxPerCycle", 200);

        private static int CycleMinutes => ReadInt("TelegramCycleMinutes", 30);

        // Nghi giua cac chunk trong cung chu ky, tranh ban lien tiep.
        private static int DelayBetweenChunksSeconds => ReadInt("TelegramDelayBetweenChunksSeconds", 5);

        private static CancellationTokenSource _cts;
        private static Task _loop;

        public static void Start()
        {
            if (_loop != null && !_loop.IsCompleted)
            {
                Log.Information("[TELEGRAM QUEUE] Dispatcher dang chay roi, bo qua lenh Start.");
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoop(_cts.Token));
            Log.Information($"[TELEGRAM QUEUE] Dispatcher da khoi dong: {ChunkSize} so/message, toi da {MaxPerCycle} so moi {CycleMinutes} phut.");
        }

        public static void Stop()
        {
            _cts?.Cancel();
        }

        private static async Task RunLoop(CancellationToken token)
        {
            await TelegramHelper.HealthCheck();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunOneCycle(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Mot chu ky loi khong duoc lam chet vong lap - so van nam trong DB, luot sau gui lai.
                    Log.Error("[TELEGRAM QUEUE] Loi trong chu ky gui: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(CycleMinutes), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Log.Information("[TELEGRAM QUEUE] Dispatcher da dung.");
        }

        /// <summary>
        /// Gui cho den khi het hang doi HOAC cham tran MaxPerCycle, tuy cai nao den truoc.
        /// </summary>
        private static async Task RunOneCycle(CancellationToken token)
        {
            int sentThisCycle = 0;

            while (!token.IsCancellationRequested && sentThisCycle < MaxPerCycle)
            {
                var batch = DBM.GetPendingTelegram(ChunkSize);
                if (batch.Count == 0)
                    break;

                bool ok = await TelegramHelper.SendTelegramMessage(BuildMessage(batch));

                if (!ok)
                {
                    // Khong danh dau gi ca -> ca lo nay o lai hang doi, luot sau retry.
                    Log.Warning($"[TELEGRAM QUEUE] Gui that bai, dung chu ky. {batch.Count} so o lai hang doi, thu lai sau {CycleMinutes} phut.");
                    return;
                }

                // Danh dau ngay sau chunk nay, khong doi het chu ky.
                DBM.MarkTelegramSent(batch.Select(b => b.PhoneNumber).ToList());
                sentThisCycle += batch.Count;

                // ListBoxSink trong FormMain bat tien to nay de do so vao o txtDsSDT.
                foreach (var item in batch)
                    Log.Information($"[TELEGRAM SENT]{item.PhoneNumber} {DateTime.Now:HH:mm:ss dd/MM/yyyy}");

                Log.Information($"[TELEGRAM QUEUE] Da gui {batch.Count} so ({sentThisCycle}/{MaxPerCycle} trong chu ky nay).");

                if (DelayBetweenChunksSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(DelayBetweenChunksSeconds), token);
            }

            if (sentThisCycle >= MaxPerCycle)
            {
                long remaining = DBM.CountPendingTelegram();
                Log.Warning($"[TELEGRAM QUEUE] Cham tran {MaxPerCycle} so/chu ky. Con {remaining} so trong hang doi, tiep tuc o chu ky sau.");
            }
        }

        /// <summary>
        /// Gom theo nguon va them mot dong ngu canh ngan. Mot khoi so dien thoai tran
        /// trong giong data dump hon la co nhan nguon, nen giu lai ten nguon + so luong.
        /// Link day du chi kem khi bat TelegramIncludeUrl.
        /// </summary>
        private static string BuildMessage(List<PendingPhone> batch)
        {
            bool includeUrl = ReadBool("TelegramIncludeUrl", false);
            var lines = new List<string>();

            // Gom theo NGUON, khong theo URL. Moi so thuong den tu mot trang khac nhau
            // (?page=1, ?page=2...), gom theo URL se ra 50 nhom "1 so moi" thay vi 1 nhom "50 so moi".
            foreach (var group in batch.GroupBy(b => SourceLabel(b.Url)))
            {
                lines.Add($"{group.Key} · {group.Count()} số mới");

                if (includeUrl)
                    foreach (var url in group.Select(b => b.Url)
                                             .Where(u => !string.IsNullOrWhiteSpace(u))
                                             .Distinct())
                        lines.Add(url);

                foreach (var item in group)
                    lines.Add(item.PhoneNumber);

                lines.Add("");
            }

            return string.Join("\n", lines).TrimEnd();
        }

        private static string SourceLabel(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Không rõ nguồn";

            try
            {
                string host = new Uri(url).Host.Replace("www.", "");
                if (host.Contains("nhatot")) return "Nhà Tốt";
                if (host.Contains("batdongsan")) return "Batdongsan";
                return host;
            }
            catch
            {
                return "Không rõ nguồn";
            }
        }

        private static int ReadInt(string key, int fallback)
        {
            int value;
            return int.TryParse(ConfigurationManager.AppSettings[key], out value) && value > 0 ? value : fallback;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            bool value;
            return bool.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback;
        }
    }
}

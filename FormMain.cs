using ScanPhoneNumber;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BDS
{
    public partial class FormMain : Form
    {
        public FormMain()
        {
            InitializeComponent();

            try
            {
                // Cấu hình Serilog
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console()                      // Ghi log ra console
                    .WriteTo.File("logs/Scraper.txt", rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 1000000) // rollingInterval: Ghi log vào file (theo ngày), fileSizeLimitBytes: dung lượng tối đa 1MB
                    .WriteTo.Sink(new ListBoxSink(listBoxLogs, txtDsSDT)) // Ghi trực tiếp vào ListBox
                    .CreateLogger();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }

            DBM.InitializeDatabase();

            string list = DBM.GetPhoneNumbersCreatedAfterOneWeek();
            txtDsSDT.Text = list; // txtPhoneNumbers là TextBox multiline
        }

        private void btnGet_Click(object sender, EventArgs e)
        {
            try
            {
                Scanner.Init();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void btnCopyAll_Click(object sender, EventArgs e)
        {
            var lines = listBoxLogs.Items.Cast<string>();
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }

        private void button1_Click(object sender, EventArgs e)
        {
            RunJobAsync();
        }
        static async Task RunJobAsync()
        {
            // Cấu hình
            int maxPage = 5;      // Số trang tối đa muốn lấy
            int delayMs = 2000;   // Delay giữa các trang (ms)
            bool headless = true;  // false = thấy Chrome chạy

            var collector = new NhatotUrlCollector();
            //await collector.InitAsync(headless);

            var urls = await collector.GetAllUrlsAsync(maxPage, delayMs);

            // In kết quả
            Console.WriteLine($"\n{'─',50}");
            Console.WriteLine($"TỔNG: {urls.Count} URL tin đăng");
            Console.WriteLine(new string('─', 50));
            foreach (var url in urls)
                Console.WriteLine(url);

            var scraper = new NhatotPlaywrightScraper();
            await scraper.InitAsync(headless: true);
            //var urls = new[]
            //{
            //    "https://www.nhatot.com/mua-ban-nha-dat-quan-binh-thanh-tp-ho-chi-minh/131258981.htm",
            //    // Thêm URL khác...
            //};
            var results = await scraper.GetPhonesAsync(urls, delayMs: 2000);
            //Console.WriteLine("\n" + new string('─', 50));
            //foreach (var r in results)
            //    Console.WriteLine($"  {r}");
            //Console.WriteLine($"\nThành công: {results.Count(r => r.Success)} / {results.Count}");
        }

    }

    public class ListBoxSink : ILogEventSink
    {
        private readonly ListBox _listBox;
        private readonly TextBox _textBox;

        public ListBoxSink(ListBox listBox, TextBox textBox)
        {
            _listBox = listBox;
            _textBox= textBox;
        }

        public void Emit(LogEvent logEvent)
        {
            if (_listBox == null) return;

            string message = logEvent.RenderMessage();
            if (message.StartsWith("[INSERT PhoneNumbersTelegram]"))
                _textBox.Invoke(new Action(() =>
                {
                    _textBox.Text = message.Replace("[INSERT PhoneNumbersTelegram]", "") + "\r\n" + _textBox.Text;
                    _listBox.TopIndex = 0; // Cuộn lên đầu
                }));
            else
                _listBox.Invoke(new Action(() =>
                {
                    _listBox.Items.Add(message);
                    _listBox.TopIndex = _listBox.Items.Count - 1; // Cuộn xuống cuối
                }));
        }
    }
}

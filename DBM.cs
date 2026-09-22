using Serilog;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BDS
{
    public class DBM
    {
        static string dbPath = "phones2.db";
        static string TablePhoneNumber = "PhoneNumbers";

        // WAL cho phep luong scanner INSERT trong khi timer Telegram UPDATE, khong bi "database is locked".
        // BusyTimeout de connection tu doi thay vi nem loi ngay khi gap lock.
        static string ConnString => $"Data Source={dbPath};Version=3;Journal Mode=WAL;BusyTimeout=5000;";

        public static void InitializeDatabase()
        {
            var conn = new SQLiteConnection(ConnString);
            conn.Open();
            var cmd = new SQLiteCommand(@"
            CREATE TABLE IF NOT EXISTS "+TablePhoneNumber +@" (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PhoneNumber TEXT UNIQUE NOT NULL,
                    Url TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(""PhoneNumber"")
                );
            CREATE INDEX IF NOT EXISTS idx_phone_number ON " + TablePhoneNumber + @"(PhoneNumber);

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

            // Kiểm tra xem cột đã tồn tại chưa
            bool columnExists = false;
            using (var cmd2 = new SQLiteCommand("PRAGMA table_info(" + TablePhoneNumber + ");", conn))
            using (var reader = cmd2.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader["name"].ToString().Equals("ProfileId", StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
            }

            // Nếu chưa có thì thêm
            if (!columnExists)
            {
                using (var cmd3 = new SQLiteCommand("ALTER TABLE " + TablePhoneNumber + @" ADD COLUMN ProfileId INTEGER; " +
                                                   "CREATE INDEX IF NOT EXISTS idx_ProfileId ON " + TablePhoneNumber + @"(ProfileId);", conn))
                {
                    cmd3.ExecuteNonQuery();
                }
            }

            MigrateTelegramSentAt(conn);

            conn.Close();
        }

        /// <summary>
        /// Them cot TelegramSentAt (NULL = chua gui, co gia tri = da gui luc nao).
        /// Lan dau them cot: danh dau TOAN BO so cu la da gui, de hang doi bat dau rong.
        /// Neu khong backfill, tick dau tien cua timer se co gui lai ca ~88k so cu.
        /// Backfill nam trong cung transaction voi ALTER va chi chay dung mot lan.
        /// </summary>
        private static void MigrateTelegramSentAt(SQLiteConnection conn)
        {
            bool exists = false;
            using (var cmd = new SQLiteCommand("PRAGMA table_info(" + TablePhoneNumber + ");", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader["name"].ToString().Equals("TelegramSentAt", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
                return;

            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(
                    "ALTER TABLE " + TablePhoneNumber + " ADD COLUMN TelegramSentAt DATETIME NULL;", conn, tx))
                {
                    cmd.ExecuteNonQuery();
                }

                long backfilled;
                using (var cmd = new SQLiteCommand(
                    "UPDATE " + TablePhoneNumber + " SET TelegramSentAt = CreatedAt WHERE TelegramSentAt IS NULL;", conn, tx))
                {
                    backfilled = cmd.ExecuteNonQuery();
                }

                // Index loc hang doi: chi cac dong chua gui, nen rat nho du bang co ~88k dong.
                using (var cmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_telegram_pending ON " + TablePhoneNumber +
                    "(CreatedAt) WHERE TelegramSentAt IS NULL;", conn, tx))
                {
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                Log.Information($"[MIGRATION] Da them cot TelegramSentAt, danh dau {backfilled} so cu la da gui.");
            }
        }

        public static string GetPhoneNumbersCreatedAfterOneWeek()
        {
            StringBuilder result = new StringBuilder();

            try
            {
                using (var conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();

                    // Doc tu PhoneNumbers.TelegramSentAt - PhoneNumbersTelegram khong con duoc ghi nua.
                    // LIMIT 500 vi bang nay co ~88k dong, do het vao TextBox se treo UI.
                    string sql = @"
                    SELECT PhoneNumber, TelegramSentAt AS CreatedAt
                    FROM PhoneNumbers
                    WHERE TelegramSentAt IS NOT NULL
                    ORDER BY TelegramSentAt DESC
                    LIMIT 500;
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

        public static string GetPhoneNumberByProfileId(long profileId)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnString))
                {
                    connection.Open();

                    using (var command = new SQLiteCommand("SELECT PhoneNumber FROM PhoneNumbers WHERE ProfileId = @ProfileId LIMIT 1;", connection))
                    {
                        command.Parameters.AddWithValue("@ProfileId", profileId);

                        var result = command.ExecuteScalar();
                        return result?.ToString() ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                // Ghi log hoặc xử lý lỗi
                Console.WriteLine($"Lỗi truy vấn SQLite: {ex.Message}");
                return string.Empty;
            }
        }
        public static bool SaveToDatabasePhoneNumber(long ProfileId, string phoneNumber, string url)
        {
            using (var conn = new SQLiteConnection(ConnString))
            {
                conn.Open();

                // Kiểm tra số đã tồn tại chưa
                var checkCmd = new SQLiteCommand($"SELECT COUNT(*) FROM {TablePhoneNumber} WHERE PhoneNumber = @phone", conn);
                checkCmd.Parameters.AddWithValue("@phone", phoneNumber);
                long count = (long)checkCmd.ExecuteScalar();

                if (count == 0)
                {
                    // Chưa có -> thêm mới
                    var insertCmd = new SQLiteCommand($"INSERT INTO {TablePhoneNumber} (PhoneNumber, Url, ProfileId) VALUES (@PhoneNumber, @Url, @ProfileId)", conn);
                    insertCmd.Parameters.AddWithValue("@PhoneNumber", phoneNumber);
                    insertCmd.Parameters.AddWithValue("@Url", url);
                    insertCmd.Parameters.AddWithValue("@ProfileId", ProfileId);
                    insertCmd.ExecuteNonQuery();
                    Log.Information($"Da luu so moi: {phoneNumber}");
                    return true;
                }
                else
                    if (ProfileId >0)
                    {
                        // đã có sđt -> cập nhật
                        var updateCmd = new SQLiteCommand($"UPDATE {TablePhoneNumber} SET Url = @Url, ProfileId = @ProfileId WHERE PhoneNumber = @PhoneNumber", conn);
                        updateCmd.Parameters.AddWithValue("@PhoneNumber", phoneNumber);
                        updateCmd.Parameters.AddWithValue("@Url", url);
                        updateCmd.Parameters.AddWithValue("@ProfileId", ProfileId);
                        updateCmd.ExecuteNonQuery();
                        Log.Information($"Cap nhat ProfileId {ProfileId} cho so dien thoai {phoneNumber}");
                        return false;
                    }
                    else
                    {
                        Log.Information($"So dien thoai {phoneNumber} da ton tai, khong luu vao database.");
                        return false;
                    }
            }
        }
       public static void SaveToDatabaseTelegram(string phoneNumber, string url)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnString))
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
            catch (Exception ex)
            {
                Log.Information("[INSERT PhoneNumbersTelegram]" + phoneNumber + ": " + ex.Message);
            }

        }

        /// <summary>
        /// Lay lo so chua gui Telegram, cu nhat truoc.
        /// </summary>
        public static List<PendingPhone> GetPendingTelegram(int limit)
        {
            var result = new List<PendingPhone>();
            try
            {
                using (var conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT PhoneNumber, Url FROM " + TablePhoneNumber +
                        " WHERE TelegramSentAt IS NULL ORDER BY CreatedAt ASC LIMIT @limit;", conn))
                    {
                        cmd.Parameters.AddWithValue("@limit", limit);
                        using (var reader = cmd.ExecuteReader())
                            while (reader.Read())
                                result.Add(new PendingPhone
                                {
                                    PhoneNumber = reader["PhoneNumber"].ToString(),
                                    Url = reader["Url"].ToString()
                                });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[TELEGRAM QUEUE] Loi doc hang doi: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Danh dau da gui cho dung lo vua gui thanh cong. Goi ngay sau MOI chunk,
        /// khong doi het luot - neu chunk sau that bai thi chunk truoc khong bi gui lai.
        /// </summary>
        public static void MarkTelegramSent(List<string> phoneNumbers)
        {
            if (phoneNumbers == null || phoneNumbers.Count == 0)
                return;

            try
            {
                using (var conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmd = new SQLiteCommand(
                            "UPDATE " + TablePhoneNumber +
                            " SET TelegramSentAt = CURRENT_TIMESTAMP WHERE PhoneNumber = @phone;", conn, tx))
                        {
                            var p = cmd.Parameters.Add("@phone", System.Data.DbType.String);
                            foreach (var phone in phoneNumbers)
                            {
                                p.Value = phone;
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                // Gui thanh cong nhung khong danh dau duoc -> luot sau gui trung. Chap nhan duoc,
                // nhung phai log de biet chu khong tuong la bug.
                Log.Error("[TELEGRAM QUEUE] Gui thanh cong nhung KHONG danh dau duoc, luot sau se gui trung: " + ex.Message);
            }
        }

        public static long CountPendingTelegram()
        {
            try
            {
                using (var conn = new SQLiteConnection(ConnString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT COUNT(*) FROM " + TablePhoneNumber + " WHERE TelegramSentAt IS NULL;", conn))
                        return (long)cmd.ExecuteScalar();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[TELEGRAM QUEUE] Loi dem hang doi: " + ex.Message);
                return -1;
            }
        }

       public static int GetLastPageNumber(string url)
       {
            using (var connection = new SQLiteConnection(ConnString))
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
        public static void SavePageNumber(int pageNumber, string url)
        {
            using (var connection = new SQLiteConnection(ConnString))
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
    }

    public class PendingPhone
    {
        public string PhoneNumber { get; set; }
        public string Url { get; set; }
    }
}

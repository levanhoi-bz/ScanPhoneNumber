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

            conn.Close();
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

        public static string GetPhoneNumberByProfileId(long profileId)
        {
            try
            {
                using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
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
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
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
                {
                    // đã có -> cập nhật
                    var updateCmd = new SQLiteCommand($"UPDATE {TablePhoneNumber} SET Url = @Url, ProfileId = @ProfileId WHERE PhoneNumber = @PhoneNumber", conn);
                    updateCmd.Parameters.AddWithValue("@PhoneNumber", phoneNumber);
                    updateCmd.Parameters.AddWithValue("@Url", url);
                    updateCmd.Parameters.AddWithValue("@ProfileId", ProfileId);
                    updateCmd.ExecuteNonQuery();
                    Log.Information($"Cap nhat ProfileId {ProfileId} cho so dien thoai {phoneNumber}");
                    return false;
                }

            }
        }
       public static void SaveToDatabaseTelegram(string phoneNumber, string url)
        {
            try
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
            catch (Exception ex)
            {
                Log.Information("[INSERT PhoneNumbersTelegram]" + phoneNumber + ": " + ex.Message);
            }

        }
       public static int GetLastPageNumber(string url)
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
        public static void SavePageNumber(int pageNumber, string url)
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
    }
}

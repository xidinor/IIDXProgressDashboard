using IIDXProgressDashboard;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IIDXProgressDashboard
{
    internal static class DbHelper
    {
        // DBファイルのパスを指定してください
        private static string connectionString = "Data Source=infinitas_log.db";

        internal static List<PlayRecord> GetHistory(string songName, string difficulty)
        {
            var list = new List<PlayRecord>();

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();

                // 部分一致検索などを考慮したSQL
                // difficultyは "SPA", "DPA" などを完全一致で検索する想定
                string sql = @"
                    SELECT song_name, difficulty_type, total_notes, score, miss_count, played_at
                    FROM play_history
                    WHERE song_name LIKE @songName
                      AND difficulty_type = @diff
                    ORDER BY played_at ASC";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@songName", "%" + songName + "%");
                    cmd.Parameters.AddWithValue("@diff", difficulty);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new PlayRecord
                            {
                                SongName = reader["song_name"].ToString(),
                                DifficultyType = reader["difficulty_type"].ToString(),
                                TotalNotes = Convert.ToInt32(reader["total_notes"]),
                                Score = Convert.ToInt32(reader["score"]),
                                MissCount = Convert.ToInt32(reader["miss_count"]),
                                PlayedAt = reader["played_at"].ToString()
                            });
                        }
                    }
                }
            }
            return list;
        }
    }
}

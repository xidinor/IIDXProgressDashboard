using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard
{
    // ランプ定義（強い順）
    public enum LampType
    {
        FullCombo = 7,
        ExHard = 6,
        Hard = 5,
        Normal = 4,   // CLEAR
        Easy = 3,
        Assist = 2,
        Failed = 1,
        NoPlay = 0
    }

    internal class LampManager
    {
        private string connectionString = "Data Source=infinitas_log.db"; // 環境に合わせてパスを確認してください
        // ※ infinitas_master.db と infinitas_log.db が別の場合、ATTACH DATABASEするか、
        // 単純に2つのコネクションを使う等の工夫が必要ですが、ここでは「同じDBファイル内」または「マージ済み」前提、
        // あるいはコード内で2つのDBを繋ぎます。
        // 今回はユーザー様の構成に合わせて「マスタDB」と「ログDB」が分かれている可能性を考慮し、
        // 単純な文字列マッチングで結合するロジックにします。

        private string masterDbString = "Data Source=infinitas_master.db";
        private string logDbString = "Data Source=infinitas_log.db";

        // ランプ文字列をEnumに変換
        public static LampType ParseLamp(string clearType)
        {
            if (string.IsNullOrEmpty(clearType)) return LampType.NoPlay;
            clearType = clearType.ToUpper();
            if (clearType.Contains("FULL")) return LampType.FullCombo;
            if (clearType.Contains("EX")) return LampType.ExHard;
            if (clearType == "HARD") return LampType.Hard;
            if (clearType == "CLEAR") return LampType.Normal;
            if (clearType == "EASY") return LampType.Easy;
            if (clearType.Contains("ASSIST")) return LampType.Assist;
            if (clearType == "FAILED") return LampType.Failed;
            return LampType.NoPlay;
        }

        // ランクごとの集計データを取得するメインメソッド
        public List<RankStats> GetRankStats(int level)
        {
            // 1. 難易度表データの取得 (Master DB)
            var targetSongs = new List<SongDifficultyInfo>();

            using (var conn = new SqliteConnection(masterDbString))
            {
                conn.Open();
                // difficulty_ranks と unofficial_difficulty を結合して取得
                // rank_name で紐付いていると仮定
                string sql = @"
                    SELECT 
                        ud.song_name, 
                        dr.display_name, 
                        dr.rank_id 
                    FROM unofficial_difficulty ud
                    JOIN difficulty_ranks dr ON ud.difficulty_rank_id = dr.rank_id
                    WHERE ud.level = @lvl
                    ORDER BY dr.rank_id DESC"; // 強いランク順

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@lvl", level);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            targetSongs.Add(new SongDifficultyInfo
                            {
                                SongName = r.GetString(0),
                                RankDisplayName = r.GetString(1),
                                RankId = r.GetInt32(2)
                            });
                        }
                    }
                }
            }

            // 2. プレイ履歴からベストランプを取得 (Log DB)
            var bestLamps = new Dictionary<string, LampType>(); // Key: SongName

            using (var conn = new SqliteConnection(logDbString))
            {
                conn.Open();
                string sql = "SELECT song_name, clear_type FROM play_history";
                using (var cmd = new SqliteCommand(sql, conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string song = r.GetString(0);
                        string cType = r.GetString(1);
                        LampType lamp = ParseLamp(cType);

                        // INFINITASの曲名を正規化（マスタとの結合率を上げるため）
                        // ※ここでは簡易的な正規化のみ行います。必要に応じて強化してください。
                        // string normSong = Normalize(song); 

                        if (!bestLamps.ContainsKey(song))
                        {
                            bestLamps[song] = lamp;
                        }
                        else
                        {
                            // より強いランプがあれば更新（ベストランプ方式）
                            if (lamp > bestLamps[song])
                            {
                                bestLamps[song] = lamp;
                            }
                        }
                    }
                }
            }

            // 3. 集計
            // ランクごとの入れ物を作る
            var statsMap = new Dictionary<string, RankStats>();

            foreach (var song in targetSongs)
            {
                if (!statsMap.ContainsKey(song.RankDisplayName))
                {
                    statsMap[song.RankDisplayName] = new RankStats
                    {
                        RankName = song.RankDisplayName,
                        SortOrder = song.RankId
                    };
                }

                // ランプ判定（曲名で紐付け）
                // ※完全一致しない場合のために、ここでも正規化比較を入れるのが理想です
                LampType currentLamp = LampType.NoPlay;
                if (bestLamps.ContainsKey(song.SongName))
                {
                    currentLamp = bestLamps[song.SongName];
                }

                statsMap[song.RankDisplayName].AddLamp(currentLamp);
            }

            // リストにしてランク順(RankId)でソートして返す
            return statsMap.Values.OrderByDescending(x => x.SortOrder).ToList();
        }

        // 内部用クラス
        private class SongDifficultyInfo
        {
            public string SongName { get; set; }
            public string RankDisplayName { get; set; }
            public int RankId { get; set; }
        }
    }

    // 集計結果保持用クラス
    public class RankStats
    {
        public string RankName { get; set; }
        public int SortOrder { get; set; }
        public int TotalCount { get; private set; }

        // ランプごとのカウント
        public int FC { get; set; }
        public int EXH { get; set; }
        public int Hard { get; set; }
        public int Normal { get; set; }
        public int Easy { get; set; }
        public int Assist { get; set; }
        public int Failed { get; set; }
        public int NoPlay { get; set; }

        public void AddLamp(LampType lamp)
        {
            TotalCount++;
            switch (lamp)
            {
                case LampType.FullCombo: FC++; break;
                case LampType.ExHard: EXH++; break;
                case LampType.Hard: Hard++; break;
                case LampType.Normal: Normal++; break;
                case LampType.Easy: Easy++; break;
                case LampType.Assist: Assist++; break;
                case LampType.Failed: Failed++; break;
                case LampType.NoPlay: NoPlay++; break;
            }
        }
    }
}

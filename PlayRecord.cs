using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IIDXProgressDashboard
{
    internal class PlayRecord
    {
        public string SongName { get; set; }
        public string DifficultyType { get; set; } // SPA, DPA etc
        public int TotalNotes { get; set; }
        public int Score { get; set; }
        public int MissCount { get; set; }

        // DBからそのまま入ってくる文字列 (例: "2025-12-20-21-08")
        public string PlayedAt { get; set; }

        // ★追加: グラフや集計で使うための変換済みプロパティ
        public DateTime PlayedAtDate
        {
            get
            {
                // フォーマットを指定してパース
                if (DateTime.TryParseExact(
                    PlayedAt,
                    "yyyy-MM-dd-HH-mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime result))
                {
                    return result;
                }

                // パース失敗時は、グラフでおかしいと気づけるように最小値を返す（あるいは現在時刻）
                return DateTime.MinValue;
            }
        }

        // スコアレート計算 (銀行家の丸め: ToEven)
        public double GetScoreRate()
        {
            if (TotalNotes == 0) return 0.0;

            // 理論値 = TotalNotes * 2
            double maxScore = TotalNotes * 2.0;
            double rate = (Score / maxScore) * 100.0;

            // 銀行家の丸め (MidpointRounding.ToEven) で小数点第2位まで丸める
            return Math.Round(rate, 2, MidpointRounding.ToEven);
        }
    }
}

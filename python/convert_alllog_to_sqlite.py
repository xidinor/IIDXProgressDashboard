import pickle
import sqlite3
import os
import unicodedata
import re
from difflib import SequenceMatcher

# === 設定 ===
pkl_file = 'alllog.pkl'
db_file = 'iidx-progress.db'
table_name = 'play_history'

# === 正規化・類似度ロジック（Wiki紐付け時のものを流用） ===
def normalize_text(text):
    if not text: return ""
    text = unicodedata.normalize('NFKC', text)
    text = text.lower()
    # INFINITAS特有の表記（もしあれば）や記号を削除
    text = re.sub(r'[\s\-_~～\(\)\[\]\.\,]', '', text)
    return text

def similarity(a, b):
    return SequenceMatcher(None, a, b).ratio()

# === マスタデータ（曲名→タグ）の読み込み ===
def load_song_master(conn):
    cursor = conn.cursor()
    cursor.execute("SELECT tag, title FROM songs")
    
    # 完全一致検索用: タイトル -> タグ の辞書
    title_map = {}
    # 高速検索用：正規化タイトル -> タグ の辞書
    norm_map = {}
    # 類似度判定用：全候補リスト
    candidates = []
    
    for row in cursor.fetchall():
        tag = row[0]
        title = row[1]
        norm = normalize_text(title)
        
        title_map[title] = tag
        norm_map[norm] = tag
        candidates.append({'tag': tag, 'title': title, 'norm': norm})
        
    return title_map, norm_map, candidates

def find_tag(song_name, title_map, norm_map, candidates):
    """
    INFINITASの曲名を受け取り、Textageのタグを返す
    1. 完全一致
    2. 正規化一致
    3. 類似度判定
    """
    if not song_name: return None
    
    norm_name = normalize_text(song_name)
    
    # 1. 完全一致
    if song_name in title_map:
        return title_map[song_name]

    # 2. 正規化一致
    if norm_name in norm_map:
        return norm_map[norm_name]
    
    # 3. 類似度判定（低速だが強力）
    best_match = None
    highest_score = 0.0
    
    for cand in candidates:
        score = similarity(norm_name, cand['norm'])
        if score > highest_score:
            highest_score = score
            best_match = cand['tag']
            
    # 閾値 0.8 (80%) 以上で一致判定
    if highest_score >= 0.8:
        print(f"Fuzzy Match: {song_name} -> {best_match} ({highest_score:.2f})")
        return best_match
        
    return None


# === メイン処理 ===
def main():
    if not os.path.exists(pkl_file):
        print(f"エラー: {pkl_file} が見つかりません。")
        return

    # 1. Pickleファイルの読み込み
    print(f"読み込み中: {pkl_file} ...")
    with open(pkl_file, 'rb') as f:
        data = pickle.load(f)
    
    print(f"データ件数: {len(data)} 件")

    # 2. データベースへの接続とテーブル作成
    conn = sqlite3.connect(db_file)
    cursor = conn.cursor()

    # 初期化のため、既存の同名テーブルがあれば削除（作り直し）
    cursor.execute(f"DROP TABLE IF EXISTS {table_name}")

    # テーブル作成
    # ユーザー提供のリスト構造に合わせてカラムを定義
    # ['11', '曲名', 'SPA', 1450, 'F', 'B', 'NO PLAY', 'E-CLEAR', 0, 1827, None, 76, '-15.00', 'OFF', '2025...']
    #   0      1       2      3     4    5       6          7      8    9     10   11      12       13       14
    create_table_query = f"""
    CREATE TABLE {table_name} (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        level TEXT,
        song_name TEXT,
        difficulty_type TEXT,
        total_notes INTEGER,
        clear_type TEXT,
        score INTEGER,
        miss_count INTEGER,
        played_option TEXT,
        played_at TEXT,
        original_data TEXT
    )
    """
    cursor.execute(create_table_query)

    # 3. データの挿入
    print("データベースへ登録中...")
    insert_count = 0
    
    # 登録用SQL
    insert_sql = f"""
    INSERT INTO {table_name} (
        level, song_name, difficulty_type, total_notes, 
        clear_type, score, miss_count, played_option, played_at, original_data
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """

    for row in data:
        # データがリスト形式で、かつ十分な長さがあるか確認
        if isinstance(row, list) and len(row) >= 15:
            try:
                # マッピング定義
                level = row[0]
                song_name = row[1]
                difficulty_type = row[2]
                total_notes = row[3]  # おそらく総ノーツ数
                # row[4]~[6] は詳細不明のため今回はスキップ（必要なら追加可能）
                clear_type = row[7]
                # row[8] 不明
                score = row[9]
                # row[10] Noneが多い？
                miss_count = row[11]
                # row[12] スコアレート等の可能性
                played_option = row[13] # 今回特定した項目
                played_at = row[14]

                # 生データ（デバッグ用に念のため文字列として全部入れておく）
                original_str = str(row)

                # 実行
                cursor.execute(insert_sql, (
                    level, song_name, difficulty_type, total_notes,
                    clear_type, score, miss_count, played_option, played_at, original_str
                ))
                insert_count += 1

            except Exception as e:
                print(f"データ変換エラー: {row} -> {e}")

    # 4. コミットとクローズ
    conn.commit()
    conn.close()

    print("-" * 40)
    print(f"完了しました。")
    print(f"保存先DB: {db_file}")
    print(f"登録件数: {insert_count} / {len(data)}")
    print("作成されたDBは 'DB Browser for SQLite' 等のツールで確認できます。")

if __name__ == "__main__":
    main()

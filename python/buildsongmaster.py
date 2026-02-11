import sqlite3
import re
import os
import ast

# === 設定 ===
DB_FILE = 'iidx-progress.db'
# 解析対象のJSファイル名
JS_FILES = {
    'title': 'titletbl.js',
    'version': 'scrlist.js',
    'notes': 'datatbl.js',
    'diff_ac': 'actbl.js',
    'diff_cs1': 'cstbl.js',
    'diff_cs2': 'cstbl1.js',
    'diff_cs3': 'cstbl2.js',
}

# 難易度・ノート数の配列インデックス定義
# Textageの配列構造: [?, SBo, SB, SN, SH, SA, SX, DB, DN, DH, DA, DX, ...]
# datatbl (ノート数) はメタデータがないため1つずれる等の調整済みマッピング
CHART_MAP = [
    # (難易度名, Style, ノート数Index, レベルIndex)
    ('BEGINNER',    'SP', 1, 3), 
    ('NORMAL',      'SP', 2, 5),
    ('HYPER',       'SP', 3, 7),
    ('ANOTHER',     'SP', 4, 9),
    ('LEGGENDARIA', 'SP', 5, 11),
    ('NORMAL',      'DP', 7, 15),
    ('HYPER',       'DP', 8, 17),
    ('ANOTHER',     'DP', 9, 19),
    ('LEGGENDARIA', 'DP', 10, 21),
]

def init_db():
    if os.path.exists(DB_FILE):
        os.remove(DB_FILE)
    conn = sqlite3.connect(DB_FILE)
    cursor = conn.cursor()

    # 楽曲マスター
    cursor.execute("""
    CREATE TABLE songs (
        tag TEXT PRIMARY KEY,
        title TEXT,
        artist TEXT,
        genre TEXT,
        version_name TEXT,
        sort_index INTEGER
    )
    """)

    # 譜面マスター
    cursor.execute("""
    CREATE TABLE charts (
        tag TEXT,
        play_style TEXT,
        difficulty TEXT,
        level INTEGER,
        total_notes INTEGER,
        FOREIGN KEY(tag) REFERENCES songs(tag)
    )
    """)
    return conn

def parse_js_object(content, var_context):
    """
    JSのオブジェクト定義（{key:[...], ...}）をPythonの辞書に変換する
    evalを使うため、変数コンテキスト(A=10等)を渡す
    """
    data = {}
    # キー: 値 のパターンを抽出 (行単位で処理)
    # 例: 'tag' : [0,1,2...]
    pattern = re.compile(r"['\"]?(\w+)['\"]?\s*:\s*(\[.*?\]),?", re.DOTALL)
    
    for line in content.splitlines():
        match = pattern.search(line)
        if match:
            key = match.group(1)
            val_str = match.group(2)
            try:
                # 変数(A,B,C...)を展開してリスト化
                val = eval(val_str, {}, var_context)
                data[key] = val
            except Exception:
                pass # 解析できない行はスキップ
    return data

def parse_version_table(file_path):
    """scrlist.js から vertbl を抽出"""
    with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
        content = f.read()
    
    # vertbl = ["...", "..."]; を探す
    match = re.search(r'vertbl\s*=\s*(\[.*?\]);', content, re.DOTALL)
    if match:
        return eval(match.group(1))
    return []

def main():
    conn = init_db()
    cursor = conn.cursor()
    
    # 1. コンテキスト（JS変数）の定義
    # actbl.js 等で使われる16進数変数
    js_context = {'A': 10, 'B': 11, 'C': 12, 'D': 13, 'E': 14, 'F': 15}

    # 2. バージョン名の取得
    versions = parse_version_table(JS_FILES['version'])
    print(f"Versions loaded: {len(versions)}")

    # 3. 各データの読み込み
    print("Loading JS files...")
    
    # Title Data
    with open(JS_FILES['title'], 'r', encoding='utf-8', errors='ignore') as f:
        titles = parse_js_object(f.read(), js_context)
        
    # Notes Data
    with open(JS_FILES['notes'], 'r', encoding='utf-8', errors='ignore') as f:
        notes_map = parse_js_object(f.read(), js_context)
        
    # Level Data (AC + CS merged)
    levels_map = {}
    for diff_file in [JS_FILES['diff_ac'], JS_FILES['diff_cs1'], JS_FILES['diff_cs2'], JS_FILES['diff_cs3']]: # 必要ならリストに追加
        if os.path.exists(diff_file):
            with open(diff_file, 'r', encoding='utf-8', errors='ignore') as f:
                # 既存データを上書き結合
                levels_map.update(parse_js_object(f.read(), js_context))

    # 4. DBへの登録
    print("Building Database...")
    song_count = 0
    chart_count = 0

    for tag, t_data in titles.items():
        # t_data: [ver_idx, int_id, opt, genre, artist, title, sub...]
        ver_idx = t_data[0]
        title = t_data[5]
        # サブタイトルがある場合は結合
        if len(t_data) > 6 and isinstance(t_data[6], str):
             # HTMLタグ除去
             sub = re.sub(r'<.*?>', '', t_data[6])
             if sub: title += f" {sub}"
        
        artist = t_data[4]
        genre = t_data[3]
        ver_name = versions[ver_idx] if ver_idx < len(versions) else "Unknown"

        # Songsテーブル登録
        cursor.execute(
            "INSERT INTO songs (tag, title, artist, genre, version_name, sort_index) VALUES (?, ?, ?, ?, ?, ?)",
            (tag, title, artist, genre, ver_name, ver_idx)
        )
        song_count += 1

        # Chartsテーブル登録
        # ノート数とレベルが存在するか確認
        n_arr = notes_map.get(tag)
        l_arr = levels_map.get(tag)

        if n_arr and l_arr:
            for diff_name, style, n_idx, l_idx in CHART_MAP:
                # 配列範囲チェック
                if n_idx < len(n_arr) and l_idx < len(l_arr):
                    notes = n_arr[n_idx]
                    level = l_arr[l_idx]

                    # ノート数が0 または レベルが0の場合は譜面なしとみなす
                    if notes > 0 and level > 0:
                        cursor.execute(
                            """INSERT INTO charts (tag, play_style, difficulty, level, total_notes) 
                               VALUES (?, ?, ?, ?, ?)""",
                            (tag, style, diff_name, level, notes)
                        )
                        chart_count += 1

    conn.commit()
    conn.close()
    print(f"完了しました。")
    print(f"楽曲数: {song_count}")
    print(f"譜面数: {chart_count}")
    print(f"DBファイル: {DB_FILE}")

if __name__ == "__main__":
    main()

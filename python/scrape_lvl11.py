import requests
import cloudscraper
from bs4 import BeautifulSoup
import sqlite3
import unicodedata
import re
import os

# TODO: 11N/Hと12N/H合計4つの難易度表を一回の実行でunofficial_difficultyに入れられるようにする

# === 設定 ===
TARGET_URL = "https://w.atwiki.jp/bemani2sp11/pages/22.html"
DB_FILE = 'iidx-progress.db'
TABLE_NAME = 'unofficial_difficulty'

# === 難易度表の表記変換 ===
RANK_MAP = {
    # --- 地力枠 ---
    '地力S+':  ('S+', 100),
    '地力S':  ('S', 90),
    '地力A+': ('A+', 80),
    '地力A':  ('A', 70),
    '地力B+': ('B+', 60),
    '地力B':  ('B', 50),
    '地力C':  ('C', 40),
    '地力D':  ('D', 30),
    '地力E':  ('E', 20),
    '地力F':  ('F', 10),
    
    # --- 個人差枠 ---
    # 個人差を「地力」と混ぜるか分けるかは好みですが、
    # ここでは「個人差」というプレフィックスを残して区別します
    '個人差S+':  ('個人差S+', 95),
    '個人差S':  ('個人差S', 85),
    '個人差A+': ('個人差A+', 75),
    '個人差A':  ('個人差A', 65),
    '個人差B+': ('個人差B+', 55),
    '個人差B':  ('個人差B', 45),
    '個人差C':  ('個人差C', 35),
    '個人差D':  ('個人差D', 25),
    '個人差E':  ('個人差E', 15),
    
    # --- その他 ---
    '特殊': ('特殊', 5),
    '未定': ('未定', 0),
}

# === 曲名正規化ロジック (Textageデータとの紐付け用) ===
def normalize_text(text):
    """
    表記揺れを吸収するための正規化
    1. NFKC正規化 (全角英数→半角など)
    2. アルファベットは小文字化
    3. スペース、記号を削除
    """
    if not text: return ""
    # HTMLタグ除去（念のため）
    text = re.sub(r'<[^>]+>', '', text)
    # 括弧系の除去 (曲名の後ろの注釈などを消す: "Title (Remix)" -> "Title")
    # ※Wiki独自の注釈がある場合に有効だが、曲名の一部の場合もあるため慎重に。
    # 今回はWiki特有の「†」や「※」などを消す処理だけ入れる
    text = text.replace('†', '').replace('※', '')
    
    text = unicodedata.normalize('NFKC', text)
    text = text.lower()
    # スペースと一般的な記号を削除
    text = re.sub(r'[\s\-_~～\(\)\[\]\.\,]', '', text)
    return text

# === DB処理 ===
def get_master_data(conn):
    """
    Textageの全曲データをメモリにロードする
    Compare用辞書: { 'normalized_title': 'tag', ... }
    """
    cursor = conn.cursor()
    cursor.execute("SELECT tag, title FROM songs")
    
    master_map = {}
    for row in cursor.fetchall():
        tag = row[0]
        title = row[1]
        norm_title = normalize_text(title)
        master_map[norm_title] = {
            'tag': tag,
            'original_title': title
        }
    return master_map

def init_db(conn):
    cursor = conn.cursor()
    # 既存テーブル削除（スクレイピングのたびに洗い替え）
    cursor.execute(f"DROP TABLE IF EXISTS {TABLE_NAME}")
    # 最初から正規化ランクとソート順のカラムを持たせる
    cursor.execute(f"""
    CREATE TABLE {TABLE_NAME} (
        tag TEXT PRIMARY KEY,
        song_name TEXT,
        difficulty_rank_id INTEGER,     -- ソート用数値かつ難易度表ID
        level INTEGER,                  -- 11 or 12
        FOREIGN KEY(tag) REFERENCES songs(tag)
    )
    """)
    conn.commit()

# === スクレイピング処理 ===
def scrape_wiki(url):
    print(f"Fetching {url} ...")
    # requests の代わりに cloudscraper を使用
    scraper = cloudscraper.create_scraper() # スクレイパーのインスタンス作成
    
    try:
        res = scraper.get(url)
        res.encoding = 'utf-8'
        
        # ステータスコードチェック (200以外ならエラー)
        if res.status_code != 200:
            print(f"Error: Failed to fetch page. Status: {res.status_code}")
            return []
            
    except Exception as e:
        print(f"Error during request: {e}")
        return []

    soup = BeautifulSoup(res.text, 'html.parser')
    
    data = [] 

    # Wikiのメインコンテンツを取得
    content = soup.find(id="wikibody")
    if not content:
        # 対策を突破できてもHTML構造が変わっている可能性があるためチェック
        print("Error: id='wikibody' not found. (Content might be protected or changed)")
        return []

    current_rank = None
    current_rank_info = None
    current_raw_rank = None
    
    # h3(見出し) と table(曲リスト) を順に走査する
    # 構造: <h3>地力S</h3> ... <table>...</table>
    # <h4>S</h4> ... <table>...</table>
    
    # 全要素をフラットに見ていく
    for element in content.find_all(['h4', 'table']):
        if element.name == 'h4':
            # 見出しからランクを取得 (例: "地力S", "個人差A")
            raw_text = element.get_text(strip=True)
            # "未定" より前はスキップするためのフラグ管理などはここで行う
            # 今回は単純に取得し、後でフィルタリングも可能
            current_rank = raw_text
            print(f"Found Rank: {current_rank}")

            # 正規表現で「スペース + カッコ以降」を削除してキーを作る
            # "地力S (20曲)" -> "地力S"
            clean_key = re.sub(r'\s*\(.*$', '', raw_text)
            # マッピング定義にあるか確認
            if clean_key in RANK_MAP:
                current_raw_rank = clean_key
                current_rank_info = RANK_MAP[clean_key] # (norm, sort)を取得
                print(f"Found Target Rank: {clean_key} -> {current_rank_info}")
            else:
                # 定義にないランク（更新履歴など）は無視モードにする
                current_rank_info = None
                # print(f"Skipping Rank: {clean_key}")

        elif element.name == 'table':
            if not current_rank: continue

            # テーブル内のリンク(aタグ) または テキストを取得
            # 通常、曲名は td の中にあり、リンクになっていることが多い
            tds = element.find_all('td')
            for td in tds:
                # リンクがある場合はリンクテキスト、なければtdテキスト
                a_tag = td.find('a')
                if a_tag:
                    song_name = a_tag.get_text(strip=True)
                else:
                    song_name = td.get_text(strip=True)
                
                # 空文字やヘッダ行などを除外
                if song_name and song_name != "曲名":
                    # (元のランク名(str), 正規化ランク(str), ソート順(int), 曲名(str))
                    data.append((
                        current_raw_rank, 
                        current_rank_info[0], 
                        current_rank_info[1], 
                        song_name
                    ))

    return data

# === メイン ===
def main():
    conn = sqlite3.connect(DB_FILE)
    init_db(conn)
    
    # 1. マスターデータの準備
    print("Loading Master Data...")
    master_map = get_master_data(conn)
    print(f"Master Songs: {len(master_map)}")

    # 2. Wikiスクレイピング
    wiki_data = scrape_wiki(TARGET_URL)
    print(f"Scraped Songs: {len(wiki_data)}")

    # 3. マッチングとDB登録
    cursor = conn.cursor()
    success_count = 0
    fail_count = 0
    
    # 紐付け失敗リスト（手動補正用）
    failed_list = []

    print("-" * 40)
    # スクレイピング後は (ページ上の難易度ランク表記, 対応するRANK_MAP内のランク表記, RANK_MAP内の表示順, ページ上の曲名) が取れる
    for raw_rank, rank_str, rank_id, song_name in wiki_data:
        norm_name = normalize_text(song_name)
        
        # マッチング試行
        match = master_map.get(norm_name)
        
        if match:
            # 成功: DBに登録
            tag = match['tag']
            original_title = match['original_title']
            
            # 重複チェック（Wiki内で同じ曲が複数箇所にある場合など）
            try:
                cursor.execute(f"INSERT INTO {TABLE_NAME} (tag, song_name, difficulty_rank_id, level) VALUES (?, ?, ?, ?)",
                               (tag, original_title, rank_id, 11))
                success_count += 1
            except sqlite3.IntegrityError:
                pass # 既に登録済みなら無視
        else:
            # 失敗
            fail_count += 1
            failed_list.append((raw_rank, rank_str, rank_id, song_name))

    conn.commit()
    conn.close()

    print("-" * 40)
    print(f"処理完了")
    print(f"成功: {success_count} 件")
    print(f"失敗: {fail_count} 件")
    
    if failed_list:
        # print("\n=== 紐付けに失敗した曲（一部抜粋） ===")
        print("\n=== 紐付けに失敗した曲 ===")
        # for rank, name in failed_list[:10]:
        for raw_rank, rank_str, rank_id, name in failed_list:
            print(f"[{raw_rank}] {name}")
        print("... (これらは正規化ロジックを見直すか、手動マッピング辞書を作成して解決します)")

if __name__ == "__main__":
    main()

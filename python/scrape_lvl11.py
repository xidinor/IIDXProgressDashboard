import requests
import cloudscraper
from bs4 import BeautifulSoup
from difflib import SequenceMatcher
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

# === 手動マッピング用リスト ===
# 何らかの理由で紐付けができないか、間違って紐付くものを強制的に紐付けるために使われる
# 正規化済曲名 : タグ のリスト
MANUAL_OVERRIDES = {
  #  "A": "a_amuro",
  #  "少年A": "syounen_a",
  # === 上2つは例 実際はちゃんと区別されるはず ===
  #  "キャトられ恋はモ～モク": "captivte", # 表記揺れが激しい例
  #  "gigadelic": "gigadeli",   # スペルが微妙に違う例

  "Scripted Connection⇒  N mix": "script_n", #2026/02/11現在、TextageにLEGGENDARIA譜面がない (追加は32 Pinky Crush) 一方H mixはあり、これに間違って紐付く
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

# === 類似度計算関数 ===
def similarity(a, b):
    return SequenceMatcher(None, a, b).ratio()

# === DB処理 ※古い。get_difficulty_table_master_data で完全代替予定===
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

# === 強化されたマスターデータ取得 ===
def get_difficulty_table_master_data(conn):
    """
    Master DBから「Level 11以上の譜面が存在する曲」だけを取得する。
    これにより、「同名のLevel 10以下しか存在しない曲」などに誤って紐付くのを防ぎ、
    かつ比較回数を減らして高速化する。

    ※完全同一の曲名で11以上の難易度を持つもの同士には対応できないので、そういうのは手動対応が必要
    """
    cursor = conn.cursor()
    # chartsテーブルと結合してLevel 11以上の曲のみ抽出 (SP/DP, 削除済かどうか不問)
    sql = """
    SELECT DISTINCT s.tag, s.title 
    FROM songs s
    JOIN charts c ON s.tag = c.tag
    WHERE c.level = 11 OR c.level = 12
    """
    cursor.execute(sql)
    
    master_list = []
    for row in cursor.fetchall():
        master_list.append({
            'tag': row[0],
            'title': row[1],
            'norm_title': normalize_text(row[1]) # 事前に正規化しておく
        })
    return master_list

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
            # TODO: ページからはBPM, Notes数が拾えるのでそれを拾って入れる より厳密な紐付けが可能になる

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

    # 2. Level 11以上の曲リストだけを取得
    print("Loading Level 11 or upper Master Data...")
    master_candidates = get_difficulty_table_master_data(conn)
    print(f"Target Candidates: {len(master_candidates)} songs")

    # 3. Wikiスクレイピング
    wiki_data = scrape_wiki(TARGET_URL)
    print(f"Scraped Songs: {len(wiki_data)}")

    # 4. マッチングとDB登録
    cursor = conn.cursor()
    success_count = 0
    fail_count = 0
    
    # 紐付け失敗リスト（手動補正用）
    failed_list = []

    print("-" * 40)
    # スクレイピング後は (ページ上の難易度ランク表記, 対応するRANK_MAP内のランク表記, RANK_MAP内の表示順, ページ上の曲名) が取れる
    for raw_rank, norm_rank, sort_order, song_name in wiki_data:
        norm_wiki_name = normalize_text(song_name)
        
        exact_match = None
        best_match = None
        highest_score = 0.0
        
        # 手動対応リストに定義があるか見る 正規化した名前で辞書を引く
        manual_tag = MANUAL_OVERRIDES.get(norm_wiki_name)

        if manual_tag:
            # 手動対応リストによる紐付け
            try:
                # MasterDBからそのタグの情報を引いて確定
                cursor.execute("SELECT title FROM songs WHERE tag = ?", (manual_tag,))
                row = cursor.fetchone()
                original_title = row[0]
                print(f"[Manual Override] {song_name} -> {original_title}")
                # 扱いは完全一致と同じ
                exact_match = {"tag": manual_tag, "title": original_title}
            except:
                #手動対応リストに入れているタグが正しくない
                print(f"[Manual Override FAILED] maybe tag is incorrect: {song_name} -> {original_title}")
        else:
            # B. 完全一致または類似度による紐付け 完全一致を探す
            exact_match = next((m for m in master_candidates if m['norm_title'] == norm_wiki_name), None)

        # B. 完全一致チェック (高速化のため)
        # 候補リストの中から完全一致を探す
        # exact_match = next((m for m in master_candidates if m['norm_title'] == norm_wiki_name), None)
        
        # 手動か完全一致がある場合
        if exact_match:
            best_match = exact_match
            highest_score = 1.0
        else:
            # C. 類似度チェック (完全一致しなかった場合のみ)
            # 閾値: 0.75 (75%) 以上で最も似ているものを採用
            for candidate in master_candidates:
                score = similarity(norm_wiki_name, candidate['norm_title'])
                if score > highest_score:
                    highest_score = score
                    best_match = candidate
            
            # 閾値判定 実際には類似度88%以下を除外しないと余計に拾ってきた要素を無視できない
            if highest_score < 0.89:
                best_match = None

        # 登録処理
        if best_match:
            tag = best_match['tag']
            original_title = best_match['title']
            
            # 類似度マッチの場合はログに出して確認できるようにする
            if highest_score < 1.0:
                print(f"[Fuzzy Match] Wiki: '{song_name}' <=> Master: '{original_title}' (Score: {highest_score:.2f})")

            try:
                cursor.execute(
                    f"INSERT INTO {TABLE_NAME} (tag, song_name, difficulty_rank_id, level) VALUES (?, ?, ?, ?)",
                    (tag, original_title, sort_order, 11)
                )
                success_count += 1
            except sqlite3.IntegrityError:
                pass # 既に登録済みなら無視
        else:
            fail_count += 1
            failed_list.append((raw_rank, norm_rank, sort_order, song_name)) # 必要ならここで類似度1位もログに出す

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
        for raw_rank, norm_rank, sort_order, song_name in failed_list[:10]:
            print(f"[{raw_rank}] {song_name}")
        print("... (これらは正規化ロジックを見直すか、手動マッピング辞書を作成して解決します)")

if __name__ == "__main__":
    main()

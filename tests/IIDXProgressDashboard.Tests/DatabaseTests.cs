using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

/// <summary>個人データを使わず、合成DBで初期化・制約・失敗時の保全を確認する。</summary>
public sealed class DatabaseTests : IDisposable
{
    // テストケースごとに独立した出力先を作り、並列実行でもDBを共有しない。
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "iidx-progress.db"));

    [Fact]
    public void InitializationIsRepeatableAndPreservesHistory()
    {
        // 初期化済みDBに履歴を入れて再初期化し、構造・適用履歴・保存データが維持されるか確認する。
        Database.Initialize();
        using (var connection = Database.OpenConnection()) Seed(connection);
        Database.Initialize();
        using var reopened = Database.OpenConnection();
        Assert.Equal(1L, Scalar(reopened, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM schema_migrations WHERE version=1;"));
        Assert.Equal(11L, Scalar(reopened, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table';"));
        Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM play_history;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM play_history WHERE miss_count IS NULL;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM play_history WHERE miss_count=0;"));
        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(reopened, "PRAGMA foreign_key_check;"));
    }

    // 不正な参照・重複・範囲外の値・参照先削除を、同じ手順で個別に検証する。
    [Theory]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('missing','SP','A');")]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','A');")]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','ANOTHER');")]
    [InlineData("UPDATE play_history SET clear_lamp=8;")]
    [InlineData("UPDATE play_history SET miss_count=-1;")]
    [InlineData("UPDATE play_history SET score=-1;")]
    [InlineData("UPDATE play_history SET source_record_key='first';")]
    [InlineData("DELETE FROM charts WHERE chart_id=1;")]
    [InlineData("DELETE FROM import_runs WHERE import_run_id=1;")]
    public void InvalidWritesAreRejected(string sql)
    {
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Seed(connection);
        // SQLiteの制約違反コード19で拒否され、既存の2プレイは残ることを確認する。
        Assert.Equal(19, Assert.Throws<SqliteException>(() => Execute(connection, sql)).SqliteErrorCode);
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM play_history;"));
    }

    [Fact]
    public void ChartIdentityAndGaugeTablesRemainIndependent()
    {
        // 同じ曲の別譜面と、同じ譜面のNORMAL/HARD別評価を登録する。
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Seed(connection);
        Execute(connection, """
            INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','H'),('test','SP','L'),('test','DP','A');
            INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
            VALUES (1,'sp11n','Normal',11,'SP','NORMAL','test'),(2,'sp11h','Hard',11,'SP','HARD','test');
            INSERT INTO difficulty_ranks(table_id,rank_code,display_name,rank_kind,sort_order)
            VALUES (1,'B','B','JIRIKI',2),(2,'C','C','JIRIKI',1);
            INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code) VALUES (1,1,'B'),(2,1,'C');
            """);
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM charts;"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM difficulty_table_entries WHERE chart_id=1;"));
        // 他の難易度表にしか存在しないランクを参照できないことも確認する。
        Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE difficulty_table_entries SET rank_code='B' WHERE table_id=2;"));
    }

    [Theory]
    [InlineData("CREATE TABLE play_history(id INTEGER PRIMARY KEY, song_name TEXT); INSERT INTO play_history VALUES(1,'original');")]
    [InlineData("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY); INSERT INTO schema_migrations VALUES(99);")]
    public void UnknownDatabaseIsNotModified(string sql)
    {
        // 初期化処理を使わずに旧形式・未知形式のDBを用意する。
        Directory.CreateDirectory(directory);
        using (var connection = new SqliteConnection($"Data Source={Database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, sql);
        }
        // 拒否されたことに加え、ファイル全体がバイト単位で変わっていないことを確認する。
        var before = File.ReadAllBytes(Database.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => Database.Initialize());
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Theory]
    [InlineData("UPDATE schema_migrations SET version=99 WHERE version=2;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=1;")]
    [InlineData("DELETE FROM schema_migrations;")]
    [InlineData("DROP INDEX idx_play_history_bp;")]
    public void ModifiedSchemaOrMigrationHistoryIsRejected(string sql)
    {
        // 正常な現行版を作ってから履歴やインデックスを変更し、黙って修復しないことを確認する。
        Database.Initialize();
        using (var connection = Database.OpenConnection()) Execute(connection, sql);
        var before = File.ReadAllBytes(Database.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => Database.Initialize());
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Fact]
    public void FailedInitialMigrationRollsBackDdlAndVersionRecord()
    {
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Database.DatabasePath};Pooling=False");
        connection.Open();
        // mainのスキーマ検査には現れないTEMPビューで、途中のCREATE TABLEを意図的に失敗させる。
        // それ以前に作成したテーブルも、トランザクション全体の取り消しで消えることを確認する。
        Execute(connection, "CREATE TEMP VIEW songs AS SELECT 'conflict' AS tag;");
        Assert.Throws<SqliteException>(() => new MigrationRunner().Run(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM main.sqlite_schema;"));
        // 失敗原因を除去すると、同じ接続で初期化をやり直せることを確認する。
        Execute(connection, "DROP VIEW temp.songs;");
        new MigrationRunner().Run(connection);
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionOneUpgradePreservesDataAndCreatesRestorableBackup(bool wal)
    {
        // 実際の旧DDLからv1を用意する。WAL内に残る確定済み履歴もバックアップ対象。
        using var connection = CreateVersionOne();
        if (wal) Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        Seed(connection);
        new MigrationRunner().Run(connection);
        new MigrationRunner().Run(connection);
        Assert.Equal(2L, Scalar(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM play_history WHERE chart_id=1;"));
        var backupPath = Assert.Single(Directory.GetFiles(directory, "*.bak"));
        using var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(1L, Scalar(backup, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(2L, Scalar(backup, "SELECT COUNT(*) FROM play_history;"));
        Assert.Equal(0L, Scalar(backup, "SELECT COUNT(*) FROM sqlite_schema WHERE name='external_song_ids';"));
        Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(connection, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public void FailedUpgradeRollsBackAndRetainsBackup()
    {
        using var connection = CreateVersionOne();
        Seed(connection);
        // DDL後の版記録を意図的に失敗させ、作成したテーブルまで巻き戻す。
        Execute(connection, "CREATE TEMP TRIGGER fail_version BEFORE INSERT ON main.schema_migrations BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        Assert.Throws<SqliteException>(() => new MigrationRunner().Run(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM main.sqlite_schema WHERE name='external_song_ids';"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM play_history;"));
        Assert.Single(Directory.GetFiles(directory, "*.bak"));
        Execute(connection, "DROP TRIGGER temp.fail_version;");
        new MigrationRunner().Run(connection);
        Assert.Equal(2L, Scalar(connection, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Theory]
    [InlineData("DROP INDEX idx_play_history_bp;")]
    [InlineData("UPDATE schema_migrations SET version=2;")]
    public void InvalidVersionOneIsRejectedBeforeBackup(string change)
    {
        using var connection = CreateVersionOne();
        Execute(connection, change);
        Assert.Throws<InvalidOperationException>(() => new MigrationRunner().Run(connection));
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='external_song_ids';"));
    }

    [Fact]
    public void ExternalIdentifiersHaveIndependentTitlesAndSourceScopedKeys()
    {
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Execute(connection, """
            INSERT INTO songs(tag,title,normalized_title) VALUES('anchor','Internal','INTERNAL');
            INSERT INTO external_song_ids(external_song_id,title,normalized_title,tag)
                VALUES(123,'External','EXTERNAL','anchor');
            INSERT INTO external_song_ids(source_name,external_song_id,title,normalized_title,tag)
                VALUES('OTHER',123,'Another','ANOTHER','anchor');
            UPDATE songs SET tag='renamed' WHERE tag='anchor';
            """);
        Assert.Equal("IIDX_DATA_TABLE", Scalar(connection, "SELECT source_name FROM external_song_ids WHERE title='External';"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM external_song_ids WHERE tag='renamed' AND is_active=1 AND created_at IS NOT NULL AND updated_at IS NOT NULL;"));
        Assert.Equal("Internal", Scalar(connection, "SELECT title FROM songs;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM songs;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE external_song_ids SET source_name='IIDX_DATA_TABLE';"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE external_song_ids SET tag='missing';"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE external_song_ids SET is_active=2;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE external_song_ids SET title=NULL;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE external_song_ids SET external_song_id=NULL;"));
    }

    private SqliteConnection CreateVersionOne()
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection($"Data Source={Database.DatabasePath};Foreign Keys=True;Pooling=False");
        connection.Open();
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
            "IIDXProgressDashboard.Database.Migrations.001_initial.sql")!;
        using var reader = new StreamReader(stream);
        Execute(connection, reader.ReadToEnd());
        Execute(connection, "INSERT INTO schema_migrations(version,description) VALUES(1,'Initial unified database schema');");
        return connection;
    }

    [Fact]
    public void OpenConnectionDoesNotCreateMissingDatabase()
    {
        // 通常の接続APIは、ディレクトリが存在していても欠けたDBファイルを新規作成しない。
        Directory.CreateDirectory(directory);
        Assert.Throws<SqliteException>(() => Database.OpenConnection());
        Assert.False(File.Exists(Database.DatabasePath));
    }

    // 共通fixture：同じ譜面の2プレイ。スコア低下、ランプ0、BPのNULLと0を含める。
    private static void Seed(SqliteConnection connection) => Execute(connection, """
        INSERT INTO songs(tag,title,normalized_title) VALUES ('test','Test','test');
        INSERT INTO charts(chart_id,tag,play_style,difficulty,level,total_notes) VALUES(1,'test','SP','A',11,1000);
        INSERT INTO import_runs(import_run_id,source_type,source_name,status) VALUES(1,'TEST','synthetic','SUCCESS');
        INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,source_system,source_record_key,import_run_id)
        VALUES(1,'2026-01-01T00:00:00Z',0,1600,NULL,'TEST','first',1),
              (1,'2026-01-01T00:01:00Z',5,1500,0,'TEST','second',1);
        """);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        // 件数やPRAGMAの結果など、検証に使う単一の値を取得する。
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        // 合成データの準備や、制約違反を起こすためのSQLを実行する。
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // 各テストが所有する一時ディレクトリだけを、接続の解放後に片付ける。
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

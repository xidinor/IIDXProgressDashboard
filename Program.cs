namespace IIDXProgressDashboard
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // 事前準備は明示指定時だけ実行する。通常起動は旧履歴・Refluxを取り込まない。
            if (args.Length > 0)
            {
                try
                {
                    if (args.Length != 3 || args[0] != "--prepare-beta")
                        throw new ArgumentException("Usage: --prepare-beta <新形式マスター・4表DB> <旧履歴DB>");
                    var result = Dashboard.BetaPreparation.PrepareAsync(AppContext.BaseDirectory, args[1], args[2]).GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "beta-preparation-result.txt"),
                        $"{result.Status}: read={result.Read}, imported={result.Imported}, duplicate={result.Duplicates}, unresolved={result.Unresolved}, invalid={result.Invalid}, conflict={result.Conflicts}\n");
                    return;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "beta-preparation-error.txt"), ex.ToString()); }
                    catch (Exception) { Console.Error.WriteLine(ex.Message); }
                    Environment.ExitCode = 1;
                    return;
                }
            }
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}

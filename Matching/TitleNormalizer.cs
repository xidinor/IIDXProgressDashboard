using System.Text;

namespace IIDXProgressDashboard.Matching;

/// <summary>原表記を変更せず、マスター・外部タイトル・alias共通の照合キーを作る。</summary>
public static class TitleNormalizer
{
    public const string Version = "nfkc-invariant-upper-space-v1";

    public static string Normalize(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        // NFKCの互換文字統合は候補収集用。キーの一致だけでtagの同一性を保証しない。
        var normalized = title.Normalize(NormalizationForm.FormKC).ToUpperInvariant()
            .Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }
}

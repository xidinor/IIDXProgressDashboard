# Phase文書の読み方

整理時点：2026-09-30、Beta3 `d11abab`。通常は次の**Phaseごとの統合解説**から読み始める。設計上の現行契約は[仕様](../SPECS.md)、開発時の作業ルールは[AGENTS.md](../AGENTS.md)に置く。

| Phase | 統合解説 | 主な対応Issue |
| --- | --- | --- |
| 1 DB基盤 | [Phase 1](phase1.md) | [#3](https://github.com/xidinor/IIDXProgressDashboard/issues/3) |
| 2 マスター・照合 | [Phase 2](phase2.md) | [#4](https://github.com/xidinor/IIDXProgressDashboard/issues/4)、[#13](https://github.com/xidinor/IIDXProgressDashboard/issues/13)、[#26](https://github.com/xidinor/IIDXProgressDashboard/issues/26) |
| 3 旧履歴 | [Phase 3](phase3.md) | [#12](https://github.com/xidinor/IIDXProgressDashboard/issues/12)、[#16](https://github.com/xidinor/IIDXProgressDashboard/issues/16) |
| 4 Reflux | [Phase 4](phase4.md) | [#17](https://github.com/xidinor/IIDXProgressDashboard/issues/17)、[#20](https://github.com/xidinor/IIDXProgressDashboard/issues/20) |
| 5 難易度表 | [Phase 5](phase5.md) | [#21](https://github.com/xidinor/IIDXProgressDashboard/issues/21)、[#37](https://github.com/xidinor/IIDXProgressDashboard/issues/37) |
| 6 表示・運用画面 | [Phase 6](phase6.md) | [#38](https://github.com/xidinor/IIDXProgressDashboard/issues/38) |

各統合解説の「原記録」には、分かれていた実装説明、入力契約、実データ調査、合成・実入力の検証結果、JSON証跡を列挙した。これらは当時の状態を示す詳細記録として保持し、統合解説から参照する。整理前の[SPECS.md全文](archive/specs-v1-before-consolidation.md)と[AGENTS.md全文](archive/agents-before-consolidation.md)も保存した。原記録にある「未確定」「次の工程」と現状が食い違うときは日付と後続記録を確認する。

Issueのチェックボックスは実装後に更新されていないものがある。未完了の判断にはIssue本文だけでなく、実装、Phase文書の検証時点、今回の対象範囲を照合する。
